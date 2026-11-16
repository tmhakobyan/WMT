using System.IO;
using System.Reflection;
using System.Windows;

namespace WMT.App;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        HelpText.Text = LoadHelp();
    }

    public static string LoadHelp()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Help.txt");
        if (s == null) return "Help.txt is missing from this build.";
        using var r = new StreamReader(s, System.Text.Encoding.UTF8);
        return r.ReadToEnd();
    }
}
