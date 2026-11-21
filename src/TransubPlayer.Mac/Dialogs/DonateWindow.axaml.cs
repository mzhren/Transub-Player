using System;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace TransubPlayer.Mac.Dialogs;

/// <summary>
/// 捐赠弹窗：内嵌微信/支付宝收款码（Assets 内嵌资源，avares://TransubPlayer authority
/// 必须用程序集名——命名空间是 TransubPlayer.Mac，见 MainWindow 标题栏图标的教训）。
/// </summary>
public partial class DonateWindow : Window
{
    public DonateWindow()
    {
        InitializeComponent();

        QrWeChat.Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(
            new Uri("avares://TransubPlayer/Assets/qrcode-wechat.png")));
        QrAliPay.Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(
            new Uri("avares://TransubPlayer/Assets/qrcode-alipay.png")));
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
