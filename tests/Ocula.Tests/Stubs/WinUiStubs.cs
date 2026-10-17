// WebImage.cs (linked into this project from the app) mentions two WinUI types. The tests don't use them, so these
// empty stand-ins let it compile here without pulling in the Windows App SDK. They are not part of the app.
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible = 0, Collapsed = 1 }
}

namespace Microsoft.UI.Xaml.Media.Imaging
{
    public class BitmapImage { }
}
