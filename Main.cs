using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using Dapper;
using IksAdminApi;
using MySqlConnector;
using VipCoreApi;

namespace IksAdmin_GroupToVip;

public class PluginConfig : PluginCFG<PluginConfig>
{
    // Для VIP PISEXA
    public bool VipByPisex {get; set;} = false;
    public string Host { get; set; } = "host";
	public string Database { get; set; } = "db";
	public string User { get; set; } = "user";
	public string Pass { get; set; } = "pass";
	public uint Port { get; set; } = 3306;
	public int Sid { get; set; } = 0;
    // ===
    public Dictionary<string, string> AGroupToVip {get; set;} = new() {
        ["Admin"] = "VIP"
    };
}

public class Main : AdminModule
{
    public override string ModuleName => "IksAdmin_GroupToVip";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "iks__";

    public PluginConfig Config = null!;
    public IVipCoreApi VipApi = null!;
    private PluginCapability<IVipCoreApi> _capability = new("vip:core"); 
    private List<CCSPlayerController> _vipGived = new();
    private string _dbConnString = "";

    public override void Ready()
    {
        base.Ready();
        Config = new PluginConfig().ReadOrCreate(AdminUtils.ConfigsDir + "/group_to_vip.json", new PluginConfig());
        var builder = new MySqlConnectionStringBuilder();
        builder.Server = Config.Host;
        builder.UserID = Config.User;
        builder.Password = Config.Pass;
        builder.Port = Config.Port;
        builder.Database = Config.Database;
        _dbConnString = builder.ToString();

        if (!Config.VipByPisex)
            VipApi = _capability.Get()!;

        Api.OnFullConnect += OnFullConnect;
    }

    private void OnFullConnect(string steamId, string ip)
    {
        var player = PlayersUtils.GetControllerBySteamIdUnsafe(steamId);

        if (player == null || !player.IsValid) return;

        var admin = player.Admin();
        if (admin == null || admin.Group == null) return;

        if (!Config.AGroupToVip.TryGetValue(admin.Group.Name, out var vipGroup))
        {
            return;
        }

        // Если VIP cssharp ТО:
        if (!Config.VipByPisex)
        {
            GiveVipCssharp(player, vipGroup);
            return;
        }
        // Если VIP by Pisex ТО:
        var accountId = player.AuthorizedSteamID!.AccountId;
        var name = player.PlayerName;
        Task.Run(async () => {
            var conn = new MySqlConnection(_dbConnString);
            await conn.OpenAsync();

            bool isVip = await conn.QuerySingleAsync<int>(
                @"select count(*) from vip_users where account_id=@accountId and sid=@sid",
                new {
                    accountId,
                    sid = Config.Sid
                }
            ) > 0 ; 

            if (isVip) return;

            // Выдаём випку
            await conn.QuerySingleAsync<int>(@"insert into vip_users 
            (account_id, name, lastvisit, sid, `group`, expires)
            values
            (@accountId, @name, @lastvisit, @group, @sid, @expires);
            ", new {
                accountId,
                name,
                lastvisit = AdminUtils.CurrentTimestamp(),
                sid = Config.Sid,
                group = vipGroup,
                expires = AdminUtils.CurrentTimestamp() + 60*60*24
            });
            Server.NextFrame(() => {
                _vipGived.Add(player);
                Server.ExecuteCommand("mm_reload_vip " + accountId);
            });
        });
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.AuthorizedSteamID == null) return HookResult.Continue;

        if (player.Admin() == null || player.Admin()?.Group == null) return HookResult.Continue;
        if (!_vipGived.Contains(player)) return HookResult.Continue;

        if (!Config.VipByPisex)
        {
            RemoveVipCssharp(player);
        } else {
            var accountId = player.AuthorizedSteamID.AccountId;
            Task.Run(async () => {
                var conn = new MySqlConnection(_dbConnString);
                await conn.OpenAsync();
                await conn.QueryAsync("delete from vip_users where account_id=@accountId and sid=@sid", new {
                    accountId = accountId,
                    sid = Config.Sid
                });
            });
        }
        _vipGived.Remove(player);
        return HookResult.Continue;
    }
    

    private void GiveVipCssharp(CCSPlayerController player, string vipGroup)
    {
        if (VipApi.IsClientVip(player))
        {
            // Если игрок уже VIP то мы не выдаём ему её ещё раз
            return;
        }
        VipApi.GiveClientTemporaryVip(player, vipGroup, 60*60*24);
        _vipGived.Add(player);
    }
    private void RemoveVipCssharp(CCSPlayerController player)
    {
        if (!VipApi.IsClientVip(player)) return;
        VipApi.RemoveClientVip(player);
        _vipGived.Remove(player);
    }
}
