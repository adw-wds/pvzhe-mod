using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PvzheRemote
{
    /// <summary>
    /// 联机分类：**已暂停服务**，这里只放一条通告。
    ///
    /// ★ 为什么不再直接打开 NetWindow：联机中继服务器（relay/）已停用，
    ///   房间与信令全部不可用。留着一个点下去必然失败（转圈、超时）的入口，
    ///   比明说更糟 —— 用户会以为是自己的网络问题，反复重试甚至来报障。
    ///
    /// ★ 为什么入口本身保留、不整个删掉：
    ///   老用户找不到「联机」会以为功能被静默删了，反而更困惑。
    ///   保留入口 + 点进来一句明确通告，是最少误解的做法。
    ///
    /// ★ NetWindow 的代码**没有删**：将来若恢复服务，把 ShowNet 里换回
    ///   OpenNetWindow() 就行，联机功能本身是完好的。
    /// </summary>
    public partial class MainWindow
    {
        static NetWindow? _netWin;

        void ShowNet()
        {
            ContentPanel.Children.Clear();

            var card = MakePanel();
            var sp = new StackPanel();
            sp.Children.Add(MakeText("联机", 20, FontWeights.Bold, (Brush)FindResource("TextBrush")));

            sp.Children.Add(MakeText("暂停提供联机服务",
                14, FontWeights.SemiBold, (Brush)FindResource("AccentBrush"),
                new Thickness(0, 12, 0, 10)));

            sp.Children.Add(MakeText(
                "联机中继服务器已停止运行，暂时无法创建或加入房间。\n" +
                "服务器资源已转用于插件仓库 —— 浏览和安装插件请到启动器的「插件」页。\n" +
                "修改器的其余功能（刷物、经济、战斗、存档等）全部不受影响。",
                11, FontWeights.Normal, (Brush)FindResource("SubBrush"),
                new Thickness(0, 0, 0, 10), true));

            card.Child = sp;
            ContentPanel.Children.Add(card);
        }

        /// <summary>
        /// 打开（或激活）联机独立窗口。
        /// ⚠️ 当前**没有任何地方调用它** —— 联机已暂停，入口改为通告。
        ///    保留是为了将来恢复服务时能一行改回去（见 ShowNet 的注释）。
        /// </summary>
        internal static void OpenNetWindow()
        {
            try
            {
                if (_netWin == null || !_netWin.IsLoaded)
                {
                    _netWin = new NetWindow();
                    _netWin.Show();
                }
                else
                {
                    _netWin.Activate();
                    if (_netWin.WindowState == WindowState.Minimized) _netWin.WindowState = WindowState.Normal;
                }
            }
            catch { }
        }
    }
}
