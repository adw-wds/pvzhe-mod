using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PvzheRemote
{
    /// <summary>
    /// 联机分类：只作为入口。
    /// 真正的联机界面在独立窗口 NetWindow 里（联机大厅 / 创建房间 / 加入房间 → 进入房间后直接显示房间界面）。
    /// 这里不再内嵌任何房间 UI。
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
            sp.Children.Add(MakeText("联机界面已独立成单独窗口：联机大厅 / 创建房间 / 加入房间；进入房间后窗口会直接切换成房间界面，可邀请朋友一起玩。",
                11, FontWeights.Normal, (Brush)FindResource("SubBrush"), new Thickness(0, 6, 0, 16), true));

            var b = MakeMiniBtn("打开联机窗口");
            b.MinWidth = 170;
            b.Click += (s, e) => OpenNetWindow();

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(b);
            sp.Children.Add(row);

            card.Child = sp;
            ContentPanel.Children.Add(card);
        }

        /// <summary>打开（或激活）联机独立窗口。</summary>
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
