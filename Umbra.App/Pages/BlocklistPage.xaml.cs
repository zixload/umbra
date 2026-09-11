using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Umbra.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Umbra.App.Pages;

public partial class BlocklistPage : UserControl
{
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    private BlocklistData _data = Blocklist.Load();
    private List<SavedBlocklist> _saved = SavedBlocklists.Load();
    private BlocklistData _alwaysData = AlwaysBlocklist.Load();

    public BlocklistPage()
    {
        InitializeComponent();

        AlwaysLabel.Text = Loc.T("blocklist.always");
        AlwaysDescText.Text = Loc.T("blocklist.always.desc");
        AlwaysItemsLabel.Text = Loc.T("blocklist.always.items");
        AlwaysApplicationTypeItem.Content = Loc.T("blocklist.apps");
        AlwaysWebsiteTypeItem.Content = Loc.T("blocklist.sites");
        AlwaysAddItemButton.ToolTip = Loc.T("blocklist.add");
        AlwaysPickRunningAppButton.ToolTip = Loc.T("blocklist.pick.running");
        AlwaysItemTypeBox.SelectedIndex = 0;
        UpdateAlwaysItemInput();
        RenderAlways();

        PresetsLabel.Text = Loc.T("blocklist.presets");
        BlocklistCoreLabel.Text = Loc.T("blocklist.core");
        SavedLabel.Text = Loc.T("blocklist.saved");
        NewListNameBox.PlaceholderText = Loc.T("blocklist.saved.name.placeholder");
        SaveListButton.ToolTip = Loc.T("blocklist.saved.save");
        ItemsLabel.Text = Loc.T("blocklist.items");
        ApplicationTypeItem.Content = Loc.T("blocklist.apps");
        WebsiteTypeItem.Content = Loc.T("blocklist.sites");
        AddItemButton.ToolTip = Loc.T("blocklist.add");
        PickRunningAppButton.ToolTip = Loc.T("blocklist.pick.running");
        ItemTypeBox.SelectedIndex = 0;
        UpdateItemInput();
        BlockedSearchBox.PlaceholderText = Loc.T("blocklist.search.placeholder");

        RenderPresets();
        RenderSaved();
        Render();
    }

    // Une icône par catégorie plutôt que la même pour les trois - plus lisible
    // en un coup d'œil que trois cartes identiques ne différant que le texte.
    private static readonly Dictionary<string, SymbolRegular> PresetIcons = new()
    {
        ["social"] = SymbolRegular.People24,
        ["streaming"] = SymbolRegular.Video24,
        ["games"] = SymbolRegular.XboxController24,
        ["news"] = SymbolRegular.News24,
        ["shopping"] = SymbolRegular.Cart24,
        ["messaging"] = SymbolRegular.Chat24,
    };

    private static readonly Dictionary<string, Color> PresetHoverColors = new()
    {
        ["social"] = Color.FromRgb(24, 119, 242),
        ["streaming"] = Color.FromRgb(145, 70, 255),
        ["games"] = Color.FromRgb(16, 124, 16),
        ["news"] = Color.FromRgb(196, 43, 28),
        ["shopping"] = Color.FromRgb(232, 119, 34),
        ["messaging"] = Color.FromRgb(0, 120, 212),
    };

    private void RenderPresets()
    {
        PresetsPanel.Children.Clear();
        foreach (var preset in BlocklistPresets.All)
        {
            var icon = PresetIcons.GetValueOrDefault(preset.Key, SymbolRegular.AppsList24);
            var card = new Wpf.Ui.Controls.Button
            {
                Content = BuildPresetContent(Loc.T($"blocklist.presets.{preset.Key}"), icon),
                Appearance = ControlAppearance.Secondary,
                Margin = new Thickness(0, 0, 8, 8),
                Width = 200,
                Height = 56,
                ToolTip = BuildPresetPreview(preset),
            };
            var hoverColor = PresetHoverColors.GetValueOrDefault(preset.Key, Color.FromRgb(0, 120, 212));
            card.MouseEnter += (_, _) =>
            {
                card.Background = new SolidColorBrush(hoverColor);
                card.Foreground = Brushes.White;
            };
            card.MouseLeave += (_, _) =>
            {
                card.ClearValue(Control.BackgroundProperty);
                card.ClearValue(Control.ForegroundProperty);
            };
            card.Click += (_, _) => ApplyPreset(preset);
            PresetsPanel.Children.Add(card);
        }
    }

    private static StackPanel BuildPresetPreview(BuiltInBlocklist preset)
    {
        var panel = new StackPanel { MaxWidth = 260 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("blocklist.presets.preview"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5),
        });
        foreach (var site in preset.Sites)
        {
            panel.Children.Add(new TextBlock { Text = site, FontSize = 11, Opacity = 0.72, Margin = new Thickness(0, 1, 0, 0) });
        }
        return panel;
    }

    private static Grid BuildPresetContent(string label, SymbolRegular icon)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new SymbolIcon { Symbol = icon, FontSize = 18, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var add = new SymbolIcon { Symbol = SymbolRegular.Add24, FontSize = 14, Opacity = 0.7, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(add, 2);
        grid.Children.Add(add);
        return grid;
    }

    private void ApplyPreset(BuiltInBlocklist preset)
    {
        var added = 0;
        foreach (var site in preset.Sites)
        {
            if (_data.Sites.Contains(site, StringComparer.OrdinalIgnoreCase)) continue;
            _data.Sites.Add(site);
            added++;
        }
        Blocklist.Save(_data);
        Render();
        ItemsExpander.IsExpanded = true;
        PresetFeedbackText.Text = added > 0
            ? string.Format(Loc.T("blocklist.presets.added"), added)
            : Loc.T("blocklist.presets.already");
        PresetFeedbackText.Visibility = Visibility.Visible;
    }

    private void RenderSaved()
    {
        SavedList.Items.Clear();
        if (_saved.Count == 0)
        {
            SavedList.Items.Add(new TextBlock { Text = Loc.T("blocklist.saved.empty"), Opacity = 0.6 });
            return;
        }

        foreach (var list in _saved)
        {
            var editBtn = new Wpf.Ui.Controls.Button
            {
                Icon = new SymbolIcon { Symbol = SymbolRegular.Edit24 },
                Appearance = ControlAppearance.Secondary,
                ToolTip = Loc.T("blocklist.saved.edit"),
                Margin = new Thickness(0, 0, 6, 0),
            };
            editBtn.Click += (_, _) => EditSaved(list);

            var deleteBtn = new Wpf.Ui.Controls.Button
            {
                Icon = new SymbolIcon { Symbol = SymbolRegular.Delete24 },
                Appearance = ControlAppearance.Secondary,
                ToolTip = Loc.T("blocklist.saved.delete"),
            };
            deleteBtn.Click += (_, _) => DeleteSaved(list);

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(editBtn);
            actions.Children.Add(deleteBtn);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var summary = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            summary.Children.Add(new TextBlock { Text = list.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold });
            summary.Children.Add(new TextBlock { Text = $"  ·  {list.Apps.Count + list.Sites.Count}", Opacity = 0.5 });
            row.Children.Add(summary);
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            SavedList.Items.Add(new Border
            {
                Background = Res("ControlFillColorSecondaryBrush"),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 8, 8, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = row,
                ToolTip = BuildSavedListTooltip(list),
            });
        }
    }

    // Le "· N" à côté du nom ne dit pas QUOI est bloqué - un survol montre
    // le détail complet sans avoir à charger la liste pour vérifier.
    private static string BuildSavedListTooltip(SavedBlocklist list)
    {
        var lines = new List<string>();
        if (list.Apps.Count > 0) lines.Add($"{Loc.T("blocklist.apps")} : {string.Join(", ", list.Apps)}");
        if (list.Sites.Count > 0) lines.Add($"{Loc.T("blocklist.sites")} : {string.Join(", ", list.Sites)}");
        return lines.Count > 0 ? string.Join("\n", lines) : Loc.T("blocklist.saved.empty");
    }

    // Charge le profil dans la liste "de travail" ci-dessous (comme un
    // preset) et pré-remplit le nom - modifier apps/sites puis cliquer sur
    // le même bouton d'enregistrement (à côté du champ nom) écrase le profil
    // existant, SaveList_Click faisant déjà un upsert par nom. Remplace ce
    // qu'il y avait dans la liste active : acceptable, un profil se recharge
    // facilement si besoin, pas de boîte de confirmation pour ça.
    private void EditSaved(SavedBlocklist list)
    {
        _data.Apps = new List<string>(list.Apps);
        _data.Sites = new List<string>(list.Sites);
        Blocklist.Save(_data);
        NewListNameBox.Text = list.Name;
        ItemsExpander.IsExpanded = true;
        Render();
        // La liste ci-dessous est celle réellement utilisée pendant une
        // session - remplacée tout de suite (pas juste un aperçu), donc
        // autant le dire plutôt que de le faire en silence.
        PresetFeedbackText.Text = string.Format(Loc.T("blocklist.saved.editing"), list.Name);
        PresetFeedbackText.Visibility = Visibility.Visible;
    }

    private void DeleteSaved(SavedBlocklist list)
    {
        _saved.Remove(list);
        SavedBlocklists.Save(_saved);
        RenderSaved();
    }

    private void SaveList_Click(object sender, RoutedEventArgs e)
    {
        var name = NewListNameBox.Text.Trim();
        if (name.Length == 0) return;

        _saved.RemoveAll(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        _saved.Add(new SavedBlocklist { Name = name, Apps = new List<string>(_data.Apps), Sites = new List<string>(_data.Sites) });
        SavedBlocklists.Save(_saved);
        NewListNameBox.Text = "";
        RenderSaved();
    }

    private void Render()
    {
        BlockedItemsList.Items.Clear();
        var query = BlockedSearchBox?.Text.Trim() ?? "";
        foreach (var app in _data.Apps.Where(item => MatchesSearch(item, query)))
            BlockedItemsList.Items.Add(BuildRow(app, SymbolRegular.AppsListDetail24, () => RemoveApp(app)));
        foreach (var site in _data.Sites.Where(item => MatchesSearch(item, query)))
            BlockedItemsList.Items.Add(BuildRow(site, SymbolRegular.Globe24, () => RemoveSite(site)));
        if (BlockedSearchBox is not null)
            BlockedSearchBox.Visibility = _data.Apps.Count + _data.Sites.Count >= 8 ? Visibility.Visible : Visibility.Collapsed;
        ItemsLabel.Text = $"{Loc.T("blocklist.items")}  ·  {_data.Apps.Count + _data.Sites.Count}";
    }

    private static bool MatchesSearch(string value, string query) =>
        query.Length == 0 || value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void BlockedSearchBox_TextChanged(object sender, TextChangedEventArgs e) => Render();

    private static FrameworkElement BuildRow(string label, SymbolRegular icon, Action onRemove)
    {
        var removeBtn = new Wpf.Ui.Controls.Button
        {
            Icon = new SymbolIcon { Symbol = SymbolRegular.Delete24 },
            Appearance = ControlAppearance.Secondary,
            ToolTip = Loc.T("blocklist.remove"),
            Opacity = 0.62,
        };
        removeBtn.MouseEnter += (_, _) => removeBtn.Opacity = 1;
        removeBtn.MouseLeave += (_, _) => removeBtn.Opacity = 0.62;
        removeBtn.Click += (_, _) => onRemove();

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new SymbolIcon { Symbol = icon, FontSize = 15, Opacity = 0.6, Margin = new Thickness(0, 0, 10, 0) });
        var labelText = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(labelText, 1);
        row.Children.Add(labelText);
        Grid.SetColumn(removeBtn, 2);
        row.Children.Add(removeBtn);

        return new Border
        {
            Background = Res("ControlFillColorSecondaryBrush"),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 7, 7, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Child = row,
        };
    }

    private bool IsAddingApplication => ItemTypeBox.SelectedItem is ComboBoxItem { Tag: "app" };

    private void ItemTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateItemInput();

    private void UpdateItemInput()
    {
        if (NewItemBox is null || PickRunningAppButton is null) return;
        NewItemBox.PlaceholderText = Loc.T(IsAddingApplication ? "blocklist.apps.placeholder" : "blocklist.sites.placeholder");
        PickRunningAppButton.Visibility = IsAddingApplication ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddItem_Click(object sender, RoutedEventArgs e) => AddItem();
    private void NewItemBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddItem(); }

    private void AddItem()
    {
        var value = NewItemBox.Text.Trim();
        if (value.Length == 0) return;

        var isWebsite = !IsAddingApplication || Blocklist.TryNormalizeWebsiteInput(value, out _);
        var target = isWebsite ? _data.Sites : _data.Apps;
        if (isWebsite) value = Blocklist.NormalizeSiteInput(value);
        if (target.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        target.Add(value);
        Blocklist.Save(_data);
        NewItemBox.Text = "";
        Render();
    }

    private void RemoveApp(string app)
    {
        _data.Apps.Remove(app);
        Blocklist.Save(_data);
        Render();
    }

    private void PickRunningApp_Click(object sender, RoutedEventArgs e)
    {
        ItemTypeBox.SelectedIndex = 0;
        string? picked;
        try
        {
            picked = RunningAppsPickerWindow.Pick(Window.GetWindow(this));
        }
        catch (InvalidOperationException)
        {
            // A presentation failure in the picker must not terminate Umbra.
            return;
        }
        if (picked is null || _data.Apps.Contains(picked, StringComparer.OrdinalIgnoreCase)) return;
        _data.Apps.Add(picked);
        Blocklist.Save(_data);
        Render();
    }

    private void RemoveSite(string site)
    {
        _data.Sites.Remove(site);
        Blocklist.Save(_data);
        Render();
    }

    // --- Toujours bloqué (indépendant de toute session/plage, voir
    // AlwaysBlocklist) - mêmes contrôles que la liste principale ci-dessus,
    // juste une source de données et une sauvegarde différentes. Pas de
    // recherche ici : pensée comme une courte liste délibérée, pas comme la
    // liste de blocage "de travail" qui peut grossir avec chaque preset.

    private bool IsAddingAlwaysApplication => AlwaysItemTypeBox.SelectedItem is ComboBoxItem { Tag: "app" };

    private void AlwaysItemTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAlwaysItemInput();

    private void UpdateAlwaysItemInput()
    {
        if (AlwaysNewItemBox is null || AlwaysPickRunningAppButton is null) return;
        AlwaysNewItemBox.PlaceholderText = Loc.T(IsAddingAlwaysApplication ? "blocklist.apps.placeholder" : "blocklist.sites.placeholder");
        AlwaysPickRunningAppButton.Visibility = IsAddingAlwaysApplication ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderAlways()
    {
        AlwaysBlockedItemsList.Items.Clear();
        foreach (var app in _alwaysData.Apps)
            AlwaysBlockedItemsList.Items.Add(BuildRow(app, SymbolRegular.AppsListDetail24, () => RemoveAlwaysApp(app)));
        foreach (var site in _alwaysData.Sites)
            AlwaysBlockedItemsList.Items.Add(BuildRow(site, SymbolRegular.Globe24, () => RemoveAlwaysSite(site)));
    }

    private void AlwaysAddItem_Click(object sender, RoutedEventArgs e) => AlwaysAddItem();
    private void AlwaysNewItemBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AlwaysAddItem(); }

    private void AlwaysAddItem()
    {
        var value = AlwaysNewItemBox.Text.Trim();
        if (value.Length == 0) return;

        var isWebsite = !IsAddingAlwaysApplication || Blocklist.TryNormalizeWebsiteInput(value, out _);
        var target = isWebsite ? _alwaysData.Sites : _alwaysData.Apps;
        if (isWebsite) value = Blocklist.NormalizeSiteInput(value);
        if (target.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        target.Add(value);
        AlwaysBlocklist.Save(_alwaysData);
        AlwaysNewItemBox.Text = "";
        RenderAlways();
        // Sans session ni plage, rien d'autre ne déclenche le watchdog - sans
        // cet appel, une première entrée ajoutée pendant que l'app tourne
        // déjà (watchdog jamais lancé) resterait sans effet jusqu'au prochain
        // redémarrage de l'app.
        WatchdogSupervisor.Ensure();
    }

    private void AlwaysPickRunningApp_Click(object sender, RoutedEventArgs e)
    {
        AlwaysItemTypeBox.SelectedIndex = 0;
        string? picked;
        try
        {
            picked = RunningAppsPickerWindow.Pick(Window.GetWindow(this));
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (picked is null || _alwaysData.Apps.Contains(picked, StringComparer.OrdinalIgnoreCase)) return;
        _alwaysData.Apps.Add(picked);
        AlwaysBlocklist.Save(_alwaysData);
        RenderAlways();
        WatchdogSupervisor.Ensure();
    }

    private void RemoveAlwaysApp(string app)
    {
        _alwaysData.Apps.Remove(app);
        AlwaysBlocklist.Save(_alwaysData);
        RenderAlways();
    }

    private void RemoveAlwaysSite(string site)
    {
        _alwaysData.Sites.Remove(site);
        AlwaysBlocklist.Save(_alwaysData);
        RenderAlways();
    }
}
