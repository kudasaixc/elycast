using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Elysium_Cast_IPTV.Services;

/// <summary>
/// Lightweight runtime localization for the WPF shell. English strings are the
/// source of truth; the French catalog is applied to already-created controls
/// without rebuilding windows or navigation state.
/// </summary>
public static class LocalizationService
{
    public const string English = "en";
    public const string French = "fr";

    private static readonly ConditionalWeakTable<DependencyObject, ElementState> States = new();
    private static readonly List<WeakReference<LocalizedProperty>> Properties = new();
    private static readonly object PropertyGate = new();
    private static bool _classHandlersRegistered;
    private static bool _applying;

    public static string CurrentLanguage { get; private set; } = English;
    public static event EventHandler? LanguageChanged;

    public static void Initialize(string? language)
    {
        CurrentLanguage = Normalize(language);
        ApplyCulture(CurrentLanguage);

        if (_classHandlersRegistered) return;
        _classHandlersRegistered = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded), true);
        EventManager.RegisterClassHandler(typeof(FrameworkContentElement), FrameworkContentElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded), true);
    }

    public static string Normalize(string? language) =>
        string.Equals(language, French, StringComparison.OrdinalIgnoreCase) ? French : English;

    public static void SetLanguage(string? language)
    {
        var normalized = Normalize(language);
        if (string.Equals(CurrentLanguage, normalized, StringComparison.Ordinal)) return;

        CurrentLanguage = normalized;
        ApplyCulture(normalized);
        ApplyRegisteredProperties();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Attach(DependencyObject root)
    {
        RegisterTree(root);
        ApplyRegisteredProperties();
    }

    public static string T(string english)
    {
        if (CurrentLanguage == French && LocalizationCatalog.TryGetFrench(english, out var french))
            return french;
        if (CurrentLanguage == French && LocalizationCatalog.TryGetFrenchTemplate(english, out french))
            return french;
        return english;
    }

    public static string Format(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

    public static bool IsCatalogText(string value) => LocalizationCatalog.ContainsEnglish(value) ||
                                                       LocalizationCatalog.TryGetEnglish(value, out _);

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject element) RegisterTree(element);
    }

    private static void RegisterTree(DependencyObject root)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<DependencyObject>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            RegisterElement(current);

            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                pending.Push(child);

            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < count; i++) pending.Push(VisualTreeHelper.GetChild(current, i));
            }
        }
    }

    private static void RegisterElement(DependencyObject element)
    {
        if (States.TryGetValue(element, out _)) return;

        var state = new ElementState();
        States.Add(element, state);

        if (element is TextBlock) RegisterProperty(element, TextBlock.TextProperty, state);
        if (element is Run) RegisterProperty(element, Run.TextProperty, state);
        if (element is ContentControl) RegisterProperty(element, ContentControl.ContentProperty, state);
        if (element is HeaderedContentControl)
            RegisterProperty(element, HeaderedContentControl.HeaderProperty, state);
        if (element is HeaderedItemsControl)
            RegisterProperty(element, HeaderedItemsControl.HeaderProperty, state);
        if (element is Window) RegisterProperty(element, Window.TitleProperty, state);
        if (element is FrameworkElement) RegisterProperty(element, FrameworkElement.TagProperty, state);
        RegisterProperty(element, ToolTipService.ToolTipProperty, state);
    }

    private static void RegisterProperty(DependencyObject owner, DependencyProperty property, ElementState state)
    {
        if (owner.ReadLocalValue(property) == DependencyProperty.UnsetValue) return;
        if (owner.GetValue(property) is not string value || !IsCatalogText(value)) return;

        var localized = new LocalizedProperty(owner, property, ToEnglish(value));
        state.Properties.Add(localized);
        lock (PropertyGate) Properties.Add(new WeakReference<LocalizedProperty>(localized));

        var descriptor = DependencyPropertyDescriptor.FromProperty(property, owner.GetType());
        if (descriptor != null)
        {
            EventHandler handler = (_, _) => OnPropertyChanged(localized);
            localized.Descriptor = descriptor;
            localized.ChangeHandler = handler;
            descriptor.AddValueChanged(owner, handler);
        }

        Apply(localized);
    }

    private static void OnPropertyChanged(LocalizedProperty localized)
    {
        if (_applying || localized.Owner.GetValue(localized.Property) is not string value) return;
        if (!IsCatalogText(value)) return;
        localized.English = ToEnglish(value);
        Apply(localized);
    }

    private static string ToEnglish(string value) =>
        LocalizationCatalog.TryGetEnglish(value, out var english) ? english : value;

    private static void ApplyRegisteredProperties()
    {
        lock (PropertyGate)
        {
            for (var i = Properties.Count - 1; i >= 0; i--)
            {
                if (!Properties[i].TryGetTarget(out var property))
                {
                    Properties.RemoveAt(i);
                    continue;
                }
                Apply(property);
            }
        }
    }

    private static void Apply(LocalizedProperty localized)
    {
        var value = T(localized.English);
        if (Equals(localized.Owner.GetValue(localized.Property), value)) return;
        try
        {
            _applying = true;
            localized.Owner.SetCurrentValue(localized.Property, value);
        }
        finally
        {
            _applying = false;
        }
    }

    private static void ApplyCulture(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language == French ? "fr-FR" : "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private sealed class ElementState
    {
        public List<LocalizedProperty> Properties { get; } = new();
    }

    private sealed class LocalizedProperty(DependencyObject owner, DependencyProperty property, string english)
    {
        public DependencyObject Owner { get; } = owner;
        public DependencyProperty Property { get; } = property;
        public string English { get; set; } = english;
        public DependencyPropertyDescriptor? Descriptor { get; set; }
        public EventHandler? ChangeHandler { get; set; }
    }
}
