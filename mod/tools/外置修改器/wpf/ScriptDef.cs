namespace PvzheRemote;

/// <summary>一个玩家脚本的元数据。</summary>
public class ScriptDef
{
    public string Name { get; set; } = "新脚本";
    public string Icon { get; set; } = "📜";
    public string Cat { get; set; } = "scripts";   // scripts/custom/system/plant/level...
    public bool Enabled { get; set; } = true;
    public string Desc { get; set; } = "";
    public string File { get; set; } = "";         // scripts/<name>.csx 相对文件名
}
