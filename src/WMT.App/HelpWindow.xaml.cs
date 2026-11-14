using System.Reflection;
using System.Windows;

namespace WMT.App;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Help.txt");
        if (s == null) { HelpText.Text = "Help.txt resource is missing from this build."; return; }
        using var r = new StreamReader(s, System.Text.Encoding.UTF8);
        HelpText.Text = r.ReadToEnd();
    }
}
