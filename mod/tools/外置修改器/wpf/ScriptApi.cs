using System;
using System.Threading.Tasks;

namespace PvzheRemote;

/// <summary>脚本全局对象：封装对游戏 MOD 的 HTTP 调用（玩家脚本可直接 await）。</summary>
public class ScriptApi
{
    public async Task<string?> CallAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return await MainWindow.GetAsync(path.StartsWith("/") ? path : "/" + path);
    }

    public Task AddSunAsync(long n) => CallAsync("/sun?add=" + n);
    public Task AddPacketAsync(string id) => CallAsync("/addpacket?id=" + Uri.EscapeDataString(id ?? ""));
    public Task SpawnAsync(string id, int row, int col) => CallAsync("/spawn?id=" + Uri.EscapeDataString(id ?? "") + "&row=" + row + "&col=" + col);
    public Task KillAllZombiesAsync() => CallAsync("/cmd?name=KillAllZombies");
    public Task KillAllPlantsAsync() => CallAsync("/cmd?name=KillAllPlants");
    public Task<string?> CmdAsync(string name) => CallAsync("/cmd?name=" + Uri.EscapeDataString(name ?? ""));
}
