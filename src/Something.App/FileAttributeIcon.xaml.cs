using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Something.App;

/// <summary>Vector outlines leave room for badges without painting over the row background.</summary>
public sealed partial class FileAttributeIcon : UserControl
{
    public static readonly DependencyProperty IsDirectoryProperty = Register(
        nameof(IsDirectory),
        false
    );
    public static readonly DependencyProperty IsHiddenProperty = Register(nameof(IsHidden), false);
    public static readonly DependencyProperty IsSystemProperty = Register(nameof(IsSystem), false);
    public static readonly DependencyProperty AttributesKnownProperty = Register(
        nameof(AttributesKnown),
        false
    );

    public bool IsDirectory
    {
        get => (bool)GetValue(IsDirectoryProperty);
        set => SetValue(IsDirectoryProperty, value);
    }

    public bool IsHidden
    {
        get => (bool)GetValue(IsHiddenProperty);
        set => SetValue(IsHiddenProperty, value);
    }

    public bool IsSystem
    {
        get => (bool)GetValue(IsSystemProperty);
        set => SetValue(IsSystemProperty, value);
    }

    public bool AttributesKnown
    {
        get => (bool)GetValue(AttributesKnownProperty);
        set => SetValue(AttributesKnownProperty, value);
    }

    private bool initialized;

    public FileAttributeIcon()
    {
        InitializeComponent();
        initialized = true;
        UpdatePresentation();
    }

    private static DependencyProperty Register(string name, bool initial) =>
        DependencyProperty.Register(
            name,
            typeof(bool),
            typeof(FileAttributeIcon),
            new PropertyMetadata(
                initial,
                (sender, _) => ((FileAttributeIcon)sender).UpdatePresentation()
            )
        );

    private void UpdatePresentation()
    {
        if (!initialized)
            return;
        bool hidden = AttributesKnown && IsHidden;
        bool system = AttributesKnown && IsSystem;
        FolderShape.Visibility = Show(IsDirectory);
        DocumentShape.Visibility = Show(!IsDirectory);
        HiddenBadge.Visibility = Show(hidden);
        SystemBadge.Visibility = Show(system);
        Canvas.SetLeft(HiddenBadge, system ? 0 : 19);
        FolderLeft.Visibility = DocumentLeft.Visibility = Show(!(hidden && system));
        FolderRight.Visibility = DocumentRight.Visibility = Show(!(hidden || system));

        string kind = IsDirectory ? "フォルダ" : "ファイル";
        string description = !AttributesKnown
            ? $"{kind}（属性不明）"
            : (hidden, system) switch
            {
                (true, true) => $"システム・隠し{kind}",
                (true, false) => $"隠し{kind}",
                (false, true) => $"システム{kind}",
                _ => kind,
            };
        ToolTipService.SetToolTip(this, description);
        AutomationProperties.SetName(this, description);
    }

    private static Visibility Show(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;
}
