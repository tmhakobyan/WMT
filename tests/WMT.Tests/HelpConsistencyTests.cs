using System.Text.RegularExpressions;
using Xunit;

namespace WMT.Tests;

public class HelpConsistencyTests
{
    [Fact] public void Every_button_menu_tab_and_column_label_in_the_UI_is_explained_in_Help()
    {
        var xaml = File.ReadAllText(T.RepoFile("src", "WMT.App", "MainWindow.xaml"));
        var help = File.ReadAllText(T.RepoFile("docs", "Help.txt"));
        var labels = Regex.Matches(xaml, "(?:Content|Header)=\"([^\"{][^\"]*)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(labels);
        var missing = labels.Where(l => !help.Contains(l, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "Help.txt does not mention: " + string.Join(", ", missing));
    }

    [Fact] public void Help_is_not_empty_and_contains_Armenian_text()
        => Assert.Matches("[\u0531-\u0587]{5,}", File.ReadAllText(T.RepoFile("docs", "Help.txt")));
}
