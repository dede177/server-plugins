using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using System.Drawing;
using System.Text.Json.Serialization;

namespace Invis;

public class InvisConfig : BasePluginConfig
{
    [JsonPropertyName("CommandPermission")] public string AdminPermission { get; set; } = "@css/generic";
}

// Invis fade timming
public struct SoundData(float startTime = -1f, float endTime = -1f)
{
    public float StartTime = startTime;
    public float EndTime = endTime;
}


public class InvisPlugin : BasePlugin, IPluginConfig<InvisConfig>
{
    public override string ModuleName => "Invis";
    public override string ModuleVersion => "1.1.0";

    public InvisConfig Config { get; set; } = new();
    
    // Track who is invis and timming
    public Dictionary<CCSPlayerController, SoundData> InvisiblePlayers = new();
    
    private CCSGameRules? _gameRules;
    private bool _gameRulesInitialized;

    public override void Load(bool hotReload)
    {
        
        // listerners from ingame events.
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnMapStart>(OnMapStartHandler);
        
        // player gets revealed if he did one of those.
        RegisterEventHandler<EventBombBeginplant>(OnPlayerStartPlant);
        RegisterEventHandler<EventBulletImpact>(OnPlayerShoot);
        RegisterEventHandler<EventPlayerSound>(OnPlayerSound);
        RegisterEventHandler<EventBombBegindefuse>(OnPlayerStartDefuse);
        RegisterEventHandler<EventWeaponReload>(OnPlayerReload);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);

        // admin commands.
        AddCommand("css_invisible", "Makes a player invisible", OnInvisibleCommand);
        AddCommand("css_invis", "Makes a player invisible", OnInvisibleCommand);

        if (hotReload)
        {
            InitializeGameRules();
        }
    }

    // cleanup when unloaded
    public override void Unload(bool hotReload)
    {
        // restore visibility for all players that were invis.
        foreach (var player in InvisiblePlayers.Keys)
        {
            if (!IsPlayerValid(player)) continue;

            var pawn = player.PlayerPawn.Value!;

            pawn.Render = Color.FromArgb(255, pawn.Render);
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

            foreach (var weapon in pawn.WeaponServices!.MyWeapons)
            {
                if (weapon.Value == null) continue;

                weapon.Value.Render = pawn.Render;
                Utilities.SetStateChanged(weapon.Value, "CBaseModelEntity", "m_clrRender");
            }
        }
        InvisiblePlayers.Clear();
    }

    public void OnConfigParsed(InvisConfig config)
    {
        Config = config;
    }

    private void OnMapStartHandler(string mapName)
    {
        _gameRules = null;
        _gameRulesInitialized = false;
    }

    private void InitializeGameRules()
    {
        if (_gameRulesInitialized) return;
        
        var gameRulesProxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        _gameRules = gameRulesProxy?.GameRules;
        _gameRulesInitialized = _gameRules != null;
    }


    // func gets called every tick for fade effects.
    public void OnTick()
    {
        if (!_gameRulesInitialized)
        {
            InitializeGameRules();
        }

        if (_gameRules != null)
        {
            _gameRules.GameRestart = _gameRules.RestartRoundTime < Server.CurrentTime;
        }

        float currentTime = Server.CurrentTime;
        var playersToRemove = new List<CCSPlayerController>();
        foreach (var (player, soundData) in InvisiblePlayers)
        {
            if (!IsPlayerValid(player))
            {
                playersToRemove.Add(player);
                continue;
            }

            var pawn = player.PlayerPawn.Value!;
            float alpha = 0f;

            // Check if the current time is within the "reveal" window.
            if (currentTime >= soundData.StartTime && currentTime < soundData.EndTime)
            {
                // Linearly interpolate alpha from 255 (fully visible) to 0 (fully invisible)
                // over the duration from StartTime to EndTime.
                alpha = Map(currentTime, soundData.StartTime, soundData.EndTime, 255f, 0f);
            }

            var alphaByte = (byte)Math.Clamp(alpha, 0, 255);

            // If player is fully invisible, ensure they cannot be spotted by the game engine.
            if (alphaByte == 0 && pawn.EntitySpottedState != null)
            {
                pawn.EntitySpottedState.Spotted = false;
                for (int i = 0; i < pawn.EntitySpottedState.SpottedByMask.Length; i++)
                {
                    pawn.EntitySpottedState.SpottedByMask[i] = 0;
                }
            }

            // progress bar type.
            int totalBlocks = 20;
            int visibleBlocks = (int)Map(alphaByte, 0, 255, 0, totalBlocks);

            int grayLeftBlocks = (totalBlocks - visibleBlocks) / 2;
            int grayRightBlocks = totalBlocks - visibleBlocks - grayLeftBlocks;

            var barBuilder = new System.Text.StringBuilder();

            // grey left side
            barBuilder.Append("<font color='#404040'>");
            barBuilder.Append(string.Concat(Enumerable.Repeat("&#9608;", grayLeftBlocks)));
            barBuilder.Append("</font>");

            // Red color for the visible middle part
            barBuilder.Append("<font color='#FF0000'>");
            barBuilder.Append(string.Concat(Enumerable.Repeat("&#9608;", visibleBlocks)));
            barBuilder.Append("</font>");

            // grey right side
            barBuilder.Append("<font color='#404040'>");
            barBuilder.Append(string.Concat(Enumerable.Repeat("&#9608;", grayRightBlocks)));
            barBuilder.Append("</font>");
            
            player.PrintToCenterHtml(barBuilder.ToString());

            // apply alpha weapons.
            var newColor = Color.FromArgb(alphaByte, pawn.Render);
            pawn.Render = newColor;
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

            if (pawn.WeaponServices?.MyWeapons == null) continue;

            foreach (var weaponHandle in pawn.WeaponServices.MyWeapons)
            {
                if (weaponHandle.Value != null)
                {
                    weaponHandle.Value.Render = newColor;
                    Utilities.SetStateChanged(weaponHandle.Value, "CBaseModelEntity", "m_clrRender");
                }
            }
        }

        // cleanup disconnect or tagged as invalid.
        foreach (var player in playersToRemove)
        {
            InvisiblePlayers.Remove(player);
        }
    }

   // what entity can a player see.
    public void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (infoList == null) return;

        if (InvisiblePlayers.Count == 0) return;

        var entitiesToHideFromCTs = new HashSet<CEntityInstance>();
        var entitiesToHideFromTs = new HashSet<CEntityInstance>();

        foreach (var (invisiblePlayer, _) in InvisiblePlayers)
        {
            if (!IsPlayerValid(invisiblePlayer)) continue;

            var pawn = invisiblePlayer.PlayerPawn.Value!;

            if (pawn.Render.A > 0) continue;

            // player hidden for.
            var targetHashSet = invisiblePlayer.TeamNum == (byte)CsTeam.Terrorist
                ? entitiesToHideFromCTs
                : entitiesToHideFromTs;

            // add player and weapons to hidden list.
            targetHashSet.Add(pawn);

            if (pawn.WeaponServices?.MyWeapons != null)
            {
                foreach (var weaponHandle in pawn.WeaponServices.MyWeapons)
                {
                    if (weaponHandle.Value != null)
                    {
                        targetHashSet.Add(weaponHandle.Value);
                    }
                }
            }
        }

        // nothing to hide.
        if (entitiesToHideFromCTs.Count == 0 && entitiesToHideFromTs.Count == 0) return;


        foreach (var (transmitInfo, receivingPlayer) in infoList)
        {
            if (!IsPlayerValid(receivingPlayer)) continue;

            HashSet<CEntityInstance>? entitiesToHide = null;
            switch (receivingPlayer.TeamNum)
            {
                case (byte)CsTeam.CounterTerrorist:
                    entitiesToHide = entitiesToHideFromCTs;
                    break;
                case (byte)CsTeam.Terrorist:
                    entitiesToHide = entitiesToHideFromTs;
                    break;
            }

            // if in the players team remove from transmit list.
            if (entitiesToHide?.Count > 0)
            {
                foreach (var entity in entitiesToHide)
                {
                    transmitInfo.TransmitEntities.Remove(entity);
                }
            }
        }
    }

    // command handler

    public void OnInvisibleCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, Config.AdminPermission))
        {
            caller.PrintToChat(" You do not have permission to use this command.");
            return;
        }

        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("Usage: css_invis <#userid|name|@all|@ct|@t>");
            return;
        }

        var targetArg = command.GetArg(1);
        var playersToToggle = new List<CCSPlayerController>();
        var allValidPlayers = GetValidPlayers();

        if (targetArg.StartsWith("@"))
        {
            switch (targetArg.ToLowerInvariant())
            {
                case "@all":
                    playersToToggle.AddRange(allValidPlayers);
                    break;
                case "@ct":
                    playersToToggle.AddRange(allValidPlayers.Where(p => p.TeamNum == (byte)CsTeam.CounterTerrorist));
                    break;
                case "@t":
                    playersToToggle.AddRange(allValidPlayers.Where(p => p.TeamNum == (byte)CsTeam.Terrorist));
                    break;
                default:
                    command.ReplyToCommand($"invalid traget select: {targetArg}");
                    return;
            }
        }
        else
        {
            var foundPlayers = allValidPlayers.Where(p =>
                p.UserId.ToString() == targetArg ||
                p.PlayerName.Contains(targetArg, StringComparison.OrdinalIgnoreCase)).ToList();

            if (foundPlayers.Count == 0)
            {
                command.ReplyToCommand($"player '{targetArg}' not found.");
                return;
            }

            if (foundPlayers.Count > 1)
            {
                command.ReplyToCommand($"multiple players found '{targetArg}'. be more specific or use id.");
                return;
            }
            playersToToggle.Add(foundPlayers.First());
        }

        if (playersToToggle.Count == 0)
        {
            command.ReplyToCommand("target not found.");
            return;
        }

        int madeInvisible = 0;
        int madeVisible = 0;

        foreach (var player in playersToToggle)
        {
            if (InvisiblePlayers.Remove(player))
            {
                madeVisible++;
                var pawn = player.PlayerPawn?.Value;
                if (pawn == null) continue;

                var visibleColor = Color.FromArgb(255, pawn.Render.R, pawn.Render.G, pawn.Render.B);
                pawn.Render = visibleColor;
                Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

                if (pawn.WeaponServices?.MyWeapons == null) continue;

                foreach (var weaponHandle in pawn.WeaponServices.MyWeapons)
                {
                    if (weaponHandle.Value != null)
                    {
                        weaponHandle.Value.Render = visibleColor;
                        Utilities.SetStateChanged(weaponHandle.Value, "CBaseModelEntity", "m_clrRender");
                    }
                }
            }
            else
            {
                madeInvisible++;
                InvisiblePlayers.Add(player, new SoundData());
            }
        }

        var feedbackParts = new List<string>();
        if (madeInvisible > 0) feedbackParts.Add($"made {madeInvisible} player(s) invisible");
        if (madeVisible > 0) feedbackParts.Add($"made {madeVisible} player(s) visible");

        if (feedbackParts.Any())
        {
            command.ReplyToCommand($"Successfully {string.Join(" and ", feedbackParts)}.");
        }
    }

    // action events

    public HookResult OnPlayerSound(EventPlayerSound @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsPlayerValid(player) || !InvisiblePlayers.TryGetValue(player!, out var data)) return HookResult.Continue;

        data.StartTime = Server.CurrentTime;
        data.EndTime = Server.CurrentTime + (@event.Duration * 2);
        InvisiblePlayers[player!] = data;
        return HookResult.Continue;
    }

    public HookResult OnPlayerShoot(EventBulletImpact @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsPlayerValid(player) || !InvisiblePlayers.TryGetValue(player!, out var data)) return HookResult.Continue;

        data.StartTime = Server.CurrentTime;
        data.EndTime = Server.CurrentTime + 0.5f;
        InvisiblePlayers[player!] = data;
        return HookResult.Continue;
    }

    public HookResult OnPlayerStartPlant(EventBombBeginplant @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsPlayerValid(player) || !InvisiblePlayers.TryGetValue(player!, out var data)) return HookResult.Continue;

        data.StartTime = Server.CurrentTime;
        data.EndTime = Server.CurrentTime + 1f;
        InvisiblePlayers[player!] = data;
        return HookResult.Continue;
    }

    public HookResult OnPlayerStartDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsPlayerValid(player) || !InvisiblePlayers.TryGetValue(player!, out var data)) return HookResult.Continue;

        data.StartTime = Server.CurrentTime;
        data.EndTime = Server.CurrentTime + 1f;
        InvisiblePlayers[player!] = data;
        return HookResult.Continue;
    }

    public HookResult OnPlayerReload(EventWeaponReload @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsPlayerValid(player) || !InvisiblePlayers.TryGetValue(player!, out var data)) return HookResult.Continue;

        data.StartTime = Server.CurrentTime;
        data.EndTime = Server.CurrentTime + 1.5f;
        InvisiblePlayers[player!] = data;
        return HookResult.Continue;
    }

    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsPlayerValid(player) || !InvisiblePlayers.TryGetValue(player!, out var data)) return HookResult.Continue;

        data.StartTime = Server.CurrentTime;
        data.EndTime = Server.CurrentTime + 0.5f;
        InvisiblePlayers[player!] = data;
        return HookResult.Continue;
    }

    // util methods

    public bool IsPlayerValid(CCSPlayerController? plr) 
    { 
        return plr != null && plr.IsValid && plr.PlayerPawn.IsValid;
    } 

    public List<CCSPlayerController> GetValidPlayers()
    {
        return Utilities.GetPlayers().Where(IsPlayerValid).ToList();
    }

    public float Map(float value, float fromMin, float fromMax, float toMin, float toMax)
    {
        float normalized = (value - fromMin) / (fromMax - fromMin);
        return toMin + normalized * (toMax - toMin);
    }

    public CCSPlayerController? GetPlayerByName(string name)
    {
        return GetValidPlayers().FirstOrDefault(x => x.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public void ServerPrintToChat(CCSPlayerController player, string message)
    {
        player.PrintToChat($" {ChatColors.Green}[SERVER]{ChatColors.White} {message}");
    }
}