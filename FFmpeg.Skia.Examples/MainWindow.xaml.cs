using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace FFmpeg.Skia.Examples;
/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    const string file_name = "mp4-example-video-download-full-hd-1920x1080.1min.mp4";
    SKVideo video_to_draw = new(file_name);
    

    public MainWindow()
    {
        InitializeComponent();
        video_to_draw.FrameReadyToRender += Video_to_draw_FrameReadyToRender;
    }

    private void Video_to_draw_FrameReadyToRender(object? sender, (SkiaSharp.SKBitmap frame, FFCodecFrameInfo frameInfo) e)
    {

    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
    }

    private void Button_PlaySKVideo(object sender, RoutedEventArgs e)
    {
        Videoplayer vp = new Videoplayer();
        vp.ShowDialog();
    }

    private void Button_PlayFFCodec2Skia(object sender, RoutedEventArgs e)
    {
        FF2SkiaCodecWindow f = new();
        f.ShowDialog();
    }

    private void Button_PlayMediaSource(object sender, RoutedEventArgs e)
    {
        MediaSourceWindow w = new();
        w.ShowDialog();
    }
}