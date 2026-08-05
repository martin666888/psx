using System.Globalization;
using System.Windows.Media;
using System.Xml.Linq;
using PSX.Controls;
using PSX.Models;
using PSX.Tests.Support;
using PSX.ViewModels;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AgentTabIconTests
{
    [TestMethod]
    public void IconResources_LoadDistinctGeometries_AndConverterResolvesKnownKeys()
    {
        var resourcesPath = Path.Combine(
            TestWorkspace.RepositoryRoot,
            "Themes",
            "AgentIcons.xaml");
        var document = XDocument.Load(resourcesPath);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = document
            .Descendants()
            .Where(element => element.Name.LocalName == "StreamGeometry")
            .ToDictionary(
                element => element.Attribute(xaml + "Key")?.Value ?? "",
                element => Geometry.Parse(element.Value.Trim()));

        var fallback = resources["AgentIconGeometry.Agent"];
        var claude = resources["AgentIconGeometry.Claude"];
        var kimi = resources["AgentIconGeometry.Kimi"];
        var qwen = resources["AgentIconGeometry.Qwen"];
        var qoder = resources["AgentIconGeometry.Qoder"];
        var converter = new ProviderIconConverter
        {
            DefaultIcon = fallback,
            ClaudeIcon = claude,
            KimiIcon = kimi,
            QwenIcon = qwen,
            QoderIcon = qoder
        };

        Assert.AreNotEqual(fallback.ToString(), claude.ToString());
        Assert.AreNotEqual(fallback.ToString(), kimi.ToString());
        Assert.AreNotEqual(fallback.ToString(), qwen.ToString());
        Assert.AreNotEqual(fallback.ToString(), qoder.ToString());
        Assert.AreNotEqual(claude.ToString(), kimi.ToString());
        Assert.AreNotEqual(qwen.ToString(), qoder.ToString());
        Assert.AreSame(
            claude,
            converter.Convert("claude", typeof(Geometry), null, CultureInfo.InvariantCulture));
        Assert.AreSame(
            kimi,
            converter.Convert("KIMI", typeof(Geometry), null, CultureInfo.InvariantCulture));
        Assert.AreSame(
            qwen,
            converter.Convert("qwen", typeof(Geometry), null, CultureInfo.InvariantCulture));
        Assert.AreSame(
            qoder,
            converter.Convert("QODER", typeof(Geometry), null, CultureInfo.InvariantCulture));
        Assert.AreSame(
            fallback,
            converter.Convert("unregistered-provider", typeof(Geometry), null, CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void TabBar_UsesIconKeyBinding_AndHasNoAgentStateMarker()
    {
        var tabBarPath = Path.Combine(
            TestWorkspace.RepositoryRoot,
            "Controls",
            "TabBar.xaml");
        var source = File.ReadAllText(tabBarPath);

        StringAssert.Contains(source, "Data=\"{Binding IconKey, Converter={StaticResource ProviderIconConverter}}\"");
        StringAssert.Contains(source, "QwenIcon=\"{StaticResource AgentIconGeometry.Qwen}\"");
        StringAssert.Contains(source, "QoderIcon=\"{StaticResource AgentIconGeometry.Qoder}\"");
        StringAssert.Contains(source, "x:Name=\"NewWorkspacePopup\"");
        Assert.IsFalse(source.Contains("new ContextMenu", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("AgentStateMarker", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("Text=\"●\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TabItem_StateStillUpdatesTooltipWithoutVisualMarkerContract()
    {
        var workspace = new WorkspaceDescriptor
        {
            WorkspaceId = Guid.NewGuid(),
            Kind = WorkspaceKind.Agent,
            IconKey = "claude",
            Title = "Investigation",
            ProviderName = "Claude Code",
            WorkingDirectory = @"D:\repo",
            AgentState = AgentWorkspaceState.Running
        };
        var viewModel = new TabItemViewModel(workspace, new NoOpCommand());

        Assert.AreEqual("claude", viewModel.IconKey);
        StringAssert.Contains(viewModel.ToolTip, "Running");

        workspace.AgentState = AgentWorkspaceState.Error;
        viewModel.Update(workspace);

        Assert.AreEqual(AgentWorkspaceState.Error, viewModel.AgentState);
        StringAssert.Contains(viewModel.ToolTip, "Error");
    }

    private sealed class NoOpCommand : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
