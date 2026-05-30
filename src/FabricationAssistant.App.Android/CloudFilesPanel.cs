using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using ColorStateList = Android.Content.Res.ColorStateList;

namespace FabricationAssistant.App.Android;

public sealed class CloudFilesPanel : IDisposable
{
    private const string AllProjectsLabel = "All Projects";

    private readonly CloudApiClient _client;
    private readonly List<MaterialCardView> _cards = [];
    private readonly StyledTooltipRegistry _tooltips = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private LinearLayout? _root;
    private LinearLayout? _list;
    private TextView? _status;
    private Spinner? _projectSpinner;
    private EditText? _search;
    private MaterialButton? _refreshButton;
    private CloudBrowserSnapshot _snapshot = new(Array.Empty<CloudProject>(), Array.Empty<CloudPackageSummary>());
    private string? _selectedProjectId;
    private bool _applyingProjects;
    private bool _refreshing;
    private bool _disposed;

    public CloudFilesPanel(CloudApiClient client)
    {
        _client = client;
    }

    public Action? SignInRequested { get; set; }
    public Action<CloudPackageSummary>? PackageSelected { get; set; }
    public Action<CloudPackageSummary, int>? PackageCounterSelected { get; set; }

    public void Refresh()
    {
        if (_root?.Context is { } ctx)
            _ = RefreshAsync(ctx, silent: false);
    }

    public View CreateView(Context ctx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int pad = Dp(ctx, 16);
        var scroll = new NestedScrollView(ctx)
        {
            LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent),
            FillViewport = true,
        };
        scroll.SetBackgroundResource(Resource.Color.fa_app_background);
        scroll.SetPadding(pad, pad, pad, pad);

        _root = new LinearLayout(ctx)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent),
        };

        AddHeader(ctx, _root);
        AddControls(ctx, _root);

        _status = new TextView(ctx);
        _status.SetTextSize(ComplexUnitType.Px, Dp(ctx, 13));
        _status.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        SetMarginBottom(_status, Dp(ctx, 10));
        _root.AddView(_status);

        _list = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        _root.AddView(_list);
        scroll.AddView(_root);

        _ = RefreshAsync(ctx, silent: false);
        _tooltips.AttachTree(ctx, scroll, includeStaticText: true);
        return scroll;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        foreach (MaterialCardView card in _cards)
            card.SetOnClickListener(null);
        _cards.Clear();
        _tooltips.Dispose();
        if (_refreshButton is not null)
            _refreshButton.Click -= OnRefreshClicked;
        if (_search is not null)
            _search.TextChanged -= OnSearchTextChanged;
        _root = null;
        _list = null;
        _status = null;
        _projectSpinner = null;
        _search = null;
        _refreshButton = null;
        SignInRequested = null;
        PackageSelected = null;
        PackageCounterSelected = null;
    }

    private void AddHeader(Context ctx, ViewGroup parent)
    {
        var row = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        SetMarginBottom(row, Dp(ctx, 12));

        var icon = new ImageView(ctx);
        icon.SetImageResource(Resource.Drawable.ic_cloud);
        icon.ImageTintList = ColorStateList.ValueOf(GetColor(ctx, Resource.Color.fa_accent_500));
        var iconParams = new LinearLayout.LayoutParams(Dp(ctx, 26), Dp(ctx, 26));
        iconParams.RightMargin = Dp(ctx, 10);
        row.AddView(icon, iconParams);

        var title = new TextView(ctx) { Text = "FA Cloud" };
        title.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        title.SetTextSize(ComplexUnitType.Px, Dp(ctx, 22));
        title.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        title.SetTypeface(title.Typeface, TypefaceStyle.Bold);
        row.AddView(title);

        _refreshButton = new MaterialButton(ctx, null, Resource.Attribute.materialButtonOutlinedStyle)
        {
            Text = "Refresh",
            ContentDescription = "Refresh cloud files",
        };
        _refreshButton.SetMinWidth(0);
        _refreshButton.SetTextSize(ComplexUnitType.Px, Dp(ctx, 12));
        _refreshButton.SetTextColor(GetColor(ctx, Resource.Color.fa_accent_500));
        _refreshButton.SetPadding(Dp(ctx, 10), 0, Dp(ctx, 10), 0);
        _refreshButton.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(ctx, 34));
        _refreshButton.Click += OnRefreshClicked;
        row.AddView(_refreshButton);

        parent.AddView(row);
    }

    private void AddControls(Context ctx, ViewGroup parent)
    {
        _projectSpinner = new Spinner(ctx)
        {
            ContentDescription = "Cloud project filter",
        };
        _projectSpinner.Background = CreateProjectSpinnerBackground(ctx);
        _projectSpinner.SetPadding(Dp(ctx, 12), 0, Dp(ctx, 10), 0);
        try
        {
            _projectSpinner.SetPopupBackgroundDrawable(CreateProjectDropdownBackground(ctx));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            global::Android.Util.Log.Debug("FA.Cloud", "Project dropdown background unavailable: " + ex.Message);
        }
        SetMarginBottom(_projectSpinner, Dp(ctx, 10));
        _projectSpinner.ItemSelected += (_, e) =>
        {
            if (_applyingProjects)
                return;

            if (_snapshot.Projects.Count == 0)
                return;

            if (e.Position == 0)
            {
                _selectedProjectId = null;
                AppSettings.CloudDefaultProjectName = AllProjectsLabel;
                RebuildList(ctx);
                return;
            }

            int projectIndex = e.Position - 1;
            if (projectIndex >= 0 && projectIndex < _snapshot.Projects.Count)
            {
                CloudProject project = _snapshot.Projects[projectIndex];
                _selectedProjectId = project.ProjectId;
                AppSettings.CloudDefaultProjectName = project.Name;
                RebuildList(ctx);
            }
        };
        parent.AddView(_projectSpinner, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent,
            Dp(ctx, 44)));

        _search = new EditText(ctx)
        {
            Hint = "Search part or revision",
            ContentDescription = "Search part or revision",
        };
        _search.SetSingleLine(true);
        _search.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        _search.SetHintTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        _search.TextChanged += OnSearchTextChanged;
        SetMarginBottom(_search, Dp(ctx, 12));
        parent.AddView(_search, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent,
            Dp(ctx, 44)));
    }

    private void OnRefreshClicked(object? sender, EventArgs e)
    {
        if (_root?.Context is { } ctx)
            _ = RefreshAsync(ctx, silent: false);
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_root?.Context is { } ctx)
            RebuildList(ctx);
    }

    private async Task RefreshAsync(Context ctx, bool silent)
    {
        if (_disposed || _refreshing)
            return;

        _refreshing = true;
        if (_refreshButton is not null)
            _refreshButton.Enabled = false;

        try
        {
            if (!_client.IsSignedIn)
            {
                _snapshot = new CloudBrowserSnapshot(Array.Empty<CloudProject>(), Array.Empty<CloudPackageSummary>());
                ApplyProjects(ctx);
                ShowSignInState(ctx);
                return;
            }

            if (!silent)
                SetStatus("Loading cloud files...");
            _snapshot = await _client.LoadBrowserAsync(_disposeCts.Token).ConfigureAwait(false);
            if (_disposed)
                return;

            RunOnUi(ctx, () =>
            {
                ApplyProjects(ctx);
                RebuildList(ctx);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RunOnUi(ctx, () =>
            {
                SetStatus("Cloud load failed: " + ex.GetBaseException().Message);
                _list?.RemoveAllViews();
            });
        }
        finally
        {
            _refreshing = false;
            RunOnUi(ctx, () =>
            {
                if (_refreshButton is not null)
                    _refreshButton.Enabled = true;
            });
        }
    }

    private void ApplyProjects(Context ctx)
    {
        if (_projectSpinner is null)
            return;

        string[] names = _snapshot.Projects.Count == 0
            ? ["No projects"]
            : [AllProjectsLabel, .. _snapshot.Projects.Select(project => project.Name)];
        var adapter = new ProjectSpinnerAdapter(ctx, names);

        int selected = 0;
        if (_snapshot.Projects.Count > 0)
        {
            string preferredName = AppSettings.CloudDefaultProjectName;
            if (string.Equals(preferredName, AllProjectsLabel, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(preferredName))
            {
                selected = 0;
                _selectedProjectId = null;
                AppSettings.CloudDefaultProjectName = AllProjectsLabel;
            }
            else
            {
                int projectIndex = _snapshot.Projects.ToList().FindIndex(project =>
                    string.Equals(project.Name, preferredName, StringComparison.OrdinalIgnoreCase));
                if (projectIndex >= 0)
                {
                    selected = projectIndex + 1;
                    _selectedProjectId = _snapshot.Projects[projectIndex].ProjectId;
                    AppSettings.CloudDefaultProjectName = _snapshot.Projects[projectIndex].Name;
                }
                else
                {
                    selected = 0;
                    _selectedProjectId = null;
                    AppSettings.CloudDefaultProjectName = AllProjectsLabel;
                }
            }
        }
        else
        {
            _selectedProjectId = null;
        }

        _applyingProjects = true;
        try
        {
            _projectSpinner.Adapter = adapter;
            _projectSpinner.SetSelection(selected, animate: false);
        }
        finally
        {
            _applyingProjects = false;
        }
    }

    private void ShowSignInState(Context ctx)
    {
        _list?.RemoveAllViews();
        SetStatus("Sign in to FA Cloud to list available files.");
        var card = CreateCard(ctx);
        var inner = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        inner.SetPadding(Dp(ctx, 14), Dp(ctx, 14), Dp(ctx, 14), Dp(ctx, 14));
        card.AddView(inner);

        var text = new TextView(ctx) { Text = "No active cloud session" };
        text.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        text.SetTextSize(ComplexUnitType.Px, Dp(ctx, 15));
        text.SetTypeface(text.Typeface, TypefaceStyle.Bold);
        inner.AddView(text);

        var button = new MaterialButton(ctx, null, Resource.Attribute.materialButtonOutlinedStyle)
        {
            Text = "Sign in",
            ContentDescription = "Sign in to FA Cloud",
        };
        button.SetTextColor(GetColor(ctx, Resource.Color.fa_accent_500));
        button.Click += (_, _) => SignInRequested?.Invoke();
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(ctx, 38));
        lp.TopMargin = Dp(ctx, 10);
        inner.AddView(button, lp);

        _list?.AddView(card);
        _tooltips.AttachTree(ctx, card, includeStaticText: true);
    }

    private void RebuildList(Context ctx)
    {
        if (_list is null)
            return;

        foreach (MaterialCardView card in _cards)
            card.SetOnClickListener(null);
        _cards.Clear();
        _tooltips.DisposeTree(_list);
        _list.RemoveAllViews();

        IEnumerable<CloudPackageSummary> packages = _snapshot.Packages;
        if (!string.IsNullOrWhiteSpace(_selectedProjectId))
            packages = packages.Where(package => package.ProjectId == _selectedProjectId);

        string query = _search?.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(query))
        {
            packages = packages.Where(package =>
                package.PartNumber.Contains(query, StringComparison.OrdinalIgnoreCase)
                || package.Revision.Contains(query, StringComparison.OrdinalIgnoreCase)
                || package.ProjectName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        CloudPackageSummary[] visible = packages.ToArray();
        SetStatus(visible.Length == 0 ? "No cloud files match this project/filter." : $"{visible.Length} cloud file{(visible.Length == 1 ? "" : "s")}");

        foreach (CloudPackageSummary package in visible)
            AddPackageRow(ctx, package);
    }

    private void AddPackageRow(Context ctx, CloudPackageSummary package)
    {
        var card = CreateCard(ctx);
        var clickListener = new PackageClickListener(this, package);
        card.Clickable = package.IsReadyToOpen;
        card.Focusable = package.IsReadyToOpen;
        card.Alpha = package.IsReadyToOpen ? 1f : 0.58f;
        card.ContentDescription = package.IsReadyToOpen
            ? "Open cloud file " + package.DisplayName
            : "Cloud file " + package.DisplayName + " is not ready to open";
        card.SetOnClickListener(clickListener);
        _cards.Add(card);

        var row = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
        row.Clickable = package.IsReadyToOpen;
        row.Focusable = false;
        row.SetOnClickListener(clickListener);
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(ctx, 12), Dp(ctx, 12), Dp(ctx, 12), Dp(ctx, 12));
        card.AddView(row);

        var preview = new ImageView(ctx)
        {
            ContentDescription = "Cloud file preview",
        };
        preview.Clickable = package.IsReadyToOpen;
        preview.SetOnClickListener(clickListener);
        preview.SetImageResource(Resource.Drawable.ic_cloud);
        preview.SetBackgroundColor(GetColor(ctx, Resource.Color.fa_control_background));
        preview.SetColorFilter(GetColor(ctx, Resource.Color.fa_accent_500));
        preview.SetScaleType(ImageView.ScaleType.CenterCrop);
        var imageParams = new LinearLayout.LayoutParams(Dp(ctx, 58), Dp(ctx, 58));
        imageParams.RightMargin = Dp(ctx, 12);
        row.AddView(preview, imageParams);

        var textGroup = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        textGroup.Clickable = package.IsReadyToOpen;
        textGroup.SetOnClickListener(clickListener);
        textGroup.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        row.AddView(textGroup);

        var title = new TextView(ctx)
        {
            Text = package.DisplayName,
            Ellipsize = TextUtils.TruncateAt.End,
        };
        title.SetSingleLine(true);
        title.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        title.SetTextSize(ComplexUnitType.Px, Dp(ctx, 15));
        title.SetTypeface(title.Typeface, TypefaceStyle.Bold);
        title.Clickable = package.IsReadyToOpen;
        title.SetOnClickListener(clickListener);
        textGroup.AddView(title);

        var detail = new TextView(ctx)
        {
            Text = package.DetailLine,
            Ellipsize = TextUtils.TruncateAt.End,
        };
        detail.SetSingleLine(true);
        detail.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        detail.SetTextSize(ComplexUnitType.Px, Dp(ctx, 12));
        detail.Clickable = package.IsReadyToOpen;
        detail.SetOnClickListener(clickListener);
        textGroup.AddView(detail);

        string readiness = package.IsReadyToOpen
            ? "Ready"
            : $"Not ready: {package.CurrentVersionStatus ?? "unknown"} / {package.CurrentVersionValidationStatus ?? "unknown"}";
        var status = new TextView(ctx) { Text = readiness };
        status.SetTextColor(GetColor(ctx, package.IsReadyToOpen ? Resource.Color.fa_accent_500 : Resource.Color.fa_warning));
        status.SetTextSize(ComplexUnitType.Px, Dp(ctx, 12));
        status.Clickable = package.IsReadyToOpen;
        status.SetOnClickListener(clickListener);
        textGroup.AddView(status);

        if (package.IsReadyToOpen && package.CurrentCounter > 0)
            AddCountersButton(ctx, textGroup, package);

        _list?.AddView(card);
        _tooltips.AttachTree(ctx, card, includeStaticText: true);
        _ = LoadPreviewIntoAsync(ctx, package, preview);
    }

    private void AddCountersButton(Context ctx, ViewGroup parent, CloudPackageSummary package)
    {
        var button = new MaterialButton(ctx, null, Resource.Attribute.materialButtonOutlinedStyle)
        {
            Text = "Counters",
            ContentDescription = "Choose file counter",
        };
        button.SetMinWidth(0);
        button.SetMinimumWidth(0);
        button.SetTextSize(ComplexUnitType.Px, Dp(ctx, 11));
        button.SetTextColor(GetColor(ctx, Resource.Color.fa_accent_500));
        button.SetPadding(Dp(ctx, 8), 0, Dp(ctx, 8), 0);
        button.Click += (_, _) => _ = ShowCounterPickerAsync(ctx, package);

        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(ctx, 32));
        lp.TopMargin = Dp(ctx, 6);
        parent.AddView(button, lp);
    }

    private async Task ShowCounterPickerAsync(Context ctx, CloudPackageSummary package)
    {
        if (_disposed)
            return;

        SetStatus("Loading cloud counters...");
        try
        {
            IReadOnlyList<CloudPackageVersionSummary> versions = await _client.LoadPackageVersionsAsync(
                package.PackageId,
                _disposeCts.Token).ConfigureAwait(false);
            if (_disposed)
                return;

            RunOnUi(ctx, () => ShowCounterPicker(ctx, package, versions));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RunOnUi(ctx, () => SetStatus("Could not load counters: " + ex.GetBaseException().Message));
        }
    }

    private void ShowCounterPicker(Context ctx, CloudPackageSummary package, IReadOnlyList<CloudPackageVersionSummary> versions)
    {
        CloudPackageVersionSummary[] openable = versions
            .Where(version => version.IsReadyToOpen)
            .OrderByDescending(version => version.Counter)
            .ToArray();
        if (openable.Length == 0)
        {
            SetStatus("No validated counters are available for this model.");
            return;
        }

        SetStatus($"{openable.Length} counter{(openable.Length == 1 ? "" : "s")} available");
        string[] labels = openable
            .Select(version => version.IsCurrent
                ? $"C{version.Counter} - current"
                : $"C{version.Counter} - read-only")
            .ToArray();

        AlertDialog dialog = new AlertDialog.Builder(ctx)
            .SetTitle(package.DisplayName + " counters")!
            .SetItems(labels, (_, e) =>
            {
                if (!_disposed && e.Which >= 0 && e.Which < openable.Length)
                    PackageCounterSelected?.Invoke(package, openable[e.Which].Counter);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show()!;

        Button? cancel = dialog.GetButton((int)global::Android.Content.DialogButtonType.Negative);
        _tooltips.Attach(ctx, cancel, "Cancel counter selection");
    }

    private async Task LoadPreviewIntoAsync(Context ctx, CloudPackageSummary package, ImageView preview)
    {
        try
        {
            Bitmap? bitmap = await _client.LoadPreviewAsync(package, _disposeCts.Token).ConfigureAwait(false);
            if (_disposed || bitmap is null)
                return;

            RunOnUi(ctx, () =>
            {
                preview.ClearColorFilter();
                preview.SetImageBitmap(bitmap);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("FA.Cloud", "Preview load failed: " + ex.GetBaseException().Message);
        }
    }

    private void SetStatus(string text)
    {
        if (_status is not null)
            _status.Text = text;
    }

    private static MaterialCardView CreateCard(Context ctx)
    {
        var card = new MaterialCardView(ctx)
        {
            LayoutParameters = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent,
                LinearLayout.LayoutParams.WrapContent),
        };
        ((LinearLayout.LayoutParams)card.LayoutParameters!).BottomMargin = Dp(ctx, 10);
        card.SetCardBackgroundColor(GetColor(ctx, Resource.Color.fa_surface_background));
        card.Radius = Dp(ctx, 8);
        card.StrokeWidth = Dp(ctx, 1);
        card.SetStrokeColor(ColorStateList.ValueOf(GetColor(ctx, Resource.Color.fa_border)));
        card.Elevation = 0f;
        return card;
    }

    private static Drawable CreateProjectSpinnerBackground(Context ctx)
    {
        var states = new StateListDrawable();
        states.AddState(new[] { AndroidStatePressed }, CreateProjectSpinnerFill(ctx, Color.Argb(74, 45, 212, 191), GetColor(ctx, Resource.Color.fa_accent_600)));
        states.AddState(new[] { AndroidStateHovered }, CreateProjectSpinnerFill(ctx, Color.Argb(46, 45, 212, 191), GetColor(ctx, Resource.Color.fa_accent_500)));
        states.AddState(new[] { AndroidStateFocused }, CreateProjectSpinnerFill(ctx, Color.Argb(46, 45, 212, 191), GetColor(ctx, Resource.Color.fa_accent_500)));
        states.AddState(Array.Empty<int>(), CreateProjectSpinnerFill(ctx, GetColor(ctx, Resource.Color.fa_control_background), GetColor(ctx, Resource.Color.fa_accent_700)));
        return states;
    }

    private static GradientDrawable CreateProjectDropdownBackground(Context ctx)
    {
        var background = new GradientDrawable();
        background.SetShape(ShapeType.Rectangle);
        background.SetColor(GetColor(ctx, Resource.Color.fa_surface_background));
        background.SetCornerRadius(Dp(ctx, 6));
        background.SetStroke(Dp(ctx, 1), GetColor(ctx, Resource.Color.fa_accent_700));
        return background;
    }

    private static GradientDrawable CreateProjectSpinnerFill(Context ctx, Color fillColor, Color strokeColor)
    {
        var fill = new GradientDrawable();
        fill.SetShape(ShapeType.Rectangle);
        fill.SetColor(fillColor);
        fill.SetCornerRadius(Dp(ctx, 6));
        fill.SetStroke(Dp(ctx, 1), strokeColor);
        return fill;
    }

    private static void RunOnUi(Context ctx, Action action)
    {
        if (ctx is global::Android.App.Activity activity)
            activity.RunOnUiThread(action);
        else
            action();
    }

    private static int Dp(Context ctx, float dp)
        => (int)(dp * (ctx.Resources?.DisplayMetrics?.Density ?? 1.0f));

    private static void SetMarginBottom(View view, int px)
    {
        if (view.LayoutParameters is ViewGroup.MarginLayoutParams margin)
        {
            margin.BottomMargin = px;
            view.LayoutParameters = margin;
            return;
        }

        var lp = new ViewGroup.MarginLayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent);
        lp.BottomMargin = px;
        view.LayoutParameters = lp;
    }

    private static Color GetColor(Context ctx, int resId) => new(ctx.GetColor(resId));

    private const int AndroidStateFocused = 16842908;
    private const int AndroidStatePressed = 16842919;
    private const int AndroidStateHovered = 16843623;

    private sealed class ProjectSpinnerAdapter : ArrayAdapter<string>
    {
        private readonly Context _ctx;

        public ProjectSpinnerAdapter(Context ctx, string[] values)
            : base(ctx, global::Android.Resource.Layout.SimpleSpinnerItem, values)
        {
            _ctx = ctx;
        }

        public override View GetView(int position, View? convertView, ViewGroup? parent)
            => CreateTextView(position, dropDown: false);

        public override View GetDropDownView(int position, View? convertView, ViewGroup? parent)
            => CreateTextView(position, dropDown: true);

        private TextView CreateTextView(int position, bool dropDown)
        {
            var text = new TextView(_ctx)
            {
                Text = GetItem(position) ?? "",
                Ellipsize = TextUtils.TruncateAt.End,
                Gravity = GravityFlags.CenterVertical,
            };
            text.SetSingleLine(true);
            text.SetTextColor(GetColor(_ctx, Resource.Color.fa_text_primary));
            text.SetTextSize(ComplexUnitType.Px, Dp(_ctx, dropDown ? 14 : 13));
            text.SetPadding(Dp(_ctx, 12), 0, Dp(_ctx, 12), 0);
            text.Background = dropDown ? CreateDropdownRowBackground(_ctx) : new ColorDrawable(Color.Transparent);
            text.LayoutParameters = new AbsListView.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                Dp(_ctx, dropDown ? 44 : 42));
            return text;
        }

        private static Drawable CreateDropdownRowBackground(Context ctx)
        {
            var states = new StateListDrawable();
            states.AddState(new[] { AndroidStatePressed }, CreateDropdownRowFill(ctx, Color.Argb(92, 45, 212, 191)));
            states.AddState(new[] { AndroidStateHovered }, CreateDropdownRowFill(ctx, Color.Argb(48, 45, 212, 191)));
            states.AddState(new[] { AndroidStateFocused }, CreateDropdownRowFill(ctx, Color.Argb(48, 45, 212, 191)));
            states.AddState(Array.Empty<int>(), CreateDropdownRowFill(ctx, GetColor(ctx, Resource.Color.fa_surface_background)));
            return states;
        }

        private static GradientDrawable CreateDropdownRowFill(Context ctx, Color fillColor)
        {
            var fill = new GradientDrawable();
            fill.SetShape(ShapeType.Rectangle);
            fill.SetColor(fillColor);
            fill.SetCornerRadius(Dp(ctx, 3));
            return fill;
        }
    }

    private sealed class PackageClickListener(
        CloudFilesPanel owner,
        CloudPackageSummary package) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? v)
        {
            if (!owner._disposed && package.IsReadyToOpen)
                owner.PackageSelected?.Invoke(package);
        }
    }
}
