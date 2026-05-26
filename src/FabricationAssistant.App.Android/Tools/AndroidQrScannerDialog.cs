using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using ZXing;
using ZXing.Common;

namespace FabricationAssistant.App.Android.Tools;

#pragma warning disable CS0618 // Legacy Camera is still used by the QR scanner for API 24 compatibility.
internal sealed class AndroidQrScannerDialog : Dialog, ISurfaceHolderCallback, global::Android.Hardware.Camera.IPreviewCallback
{
    private const int AndroidStateEnabled = 16842910;
    private const int AndroidStateFocused = 16842908;
    private const int AndroidStatePressed = 16842919;
    private const int AndroidStateHovered = 16843623;

    private readonly Func<string?, AndroidQrScanResult> _resolvePayload;
    private readonly Action<int> _selectMatch;
    private readonly Action<int> _isolateMatch;
    private readonly Action<int> _isolateXrayMatch;
    private readonly BarcodeReaderGeneric _reader;
    private readonly bool _isWideLayout;

    private SurfaceView? _preview;
    private AspectRatioFrameLayout? _previewFrame;
    private TextView? _status;
    private EditText? _manualInput;
    private LinearLayout? _scanStage;
    private LinearLayout? _reviewStage;
    private TextView? _reviewStatus;
    private TextView? _lastScanValue;
    private TextView? _resolvedPartValue;
    private LinearLayout? _matchesContainer;
    private TextView? _selectButton;
    private TextView? _isolateButton;
    private TextView? _isolateXrayButton;
    private TextView? _clearButton;
    private global::Android.Hardware.Camera? _camera;
    private AndroidQrScanResult _currentResult = AndroidQrScanResult.Empty("Point the camera at a Fabrication Assistant QR code.");
    private int _selectedMatchIndex = -1;
    private bool _surfaceReady;
    private bool _cameraPausedForResult;
    private int _decoding;
    private long _lastDecodeTicks;

    public AndroidQrScannerDialog(
        Context context,
        Func<string?, AndroidQrScanResult> resolvePayload,
        Action<int> selectMatch,
        Action<int> isolateMatch,
        Action<int> isolateXrayMatch)
        : base(context)
    {
        _resolvePayload = resolvePayload ?? throw new ArgumentNullException(nameof(resolvePayload));
        _selectMatch = selectMatch ?? throw new ArgumentNullException(nameof(selectMatch));
        _isolateMatch = isolateMatch ?? throw new ArgumentNullException(nameof(isolateMatch));
        _isolateXrayMatch = isolateXrayMatch ?? throw new ArgumentNullException(nameof(isolateXrayMatch));
        float widthDp = (context.Resources?.DisplayMetrics?.WidthPixels ?? 0)
                        / Math.Max(context.Resources?.DisplayMetrics?.Density ?? 1.0f, 1.0f);
        _isWideLayout = widthDp >= 680.0f;
        _reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = [BarcodeFormat.QR_CODE],
            },
        };
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestWindowFeature((int)WindowFeatures.NoTitle);
        Window?.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        Window?.SetSoftInputMode(SoftInput.AdjustResize);

        var root = new LinearLayout(Context)
        {
            Orientation = Orientation.Vertical,
            Background = CreateDialogBackground(),
        };
        root.SetPadding(Dp(12), Dp(12), Dp(12), Dp(12));

        _scanStage = CreateScannerPanel();
        _reviewStage = CreateResultPanel();
        _reviewStage.Visibility = ViewStates.Gone;

        root.AddView(_scanStage, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));
        root.AddView(_reviewStage, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        var scroll = new ScrollView(Context)
        {
            FillViewport = false,
            Background = new ColorDrawable(Color.Transparent),
        };
        scroll.AddView(root, new ScrollView.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        SetContentView(scroll);
        RenderResult();
    }

    protected override void OnStart()
    {
        base.OnStart();

        int screenWidth = Context.Resources?.DisplayMetrics?.WidthPixels ?? Dp(380);
        int maxWidth = _isWideLayout ? Dp(620) : Dp(430);
        int sideMargin = _isWideLayout ? Dp(96) : Dp(28);
        int width = Math.Min(maxWidth, Math.Max(Dp(300), screenWidth - sideMargin));
        Window?.SetLayout(width, ViewGroup.LayoutParams.WrapContent);

        if (_surfaceReady && !_cameraPausedForResult)
            StartCamera();
    }

    protected override void OnStop()
    {
        StopCamera();
        base.OnStop();
    }

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        _surfaceReady = true;
        if (!_cameraPausedForResult)
            StartCamera();
    }

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
    {
        if (_camera is not null)
        {
            StopCamera();
            if (!_cameraPausedForResult)
                StartCamera();
        }
    }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        _surfaceReady = false;
        StopCamera();
    }

    public void OnPreviewFrame(byte[]? data, global::Android.Hardware.Camera? camera)
    {
        if (_cameraPausedForResult || data is null || camera is null)
            return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsedMs = (now - Interlocked.Read(ref _lastDecodeTicks)) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (elapsedMs < 180.0 || Interlocked.CompareExchange(ref _decoding, 1, 0) != 0)
            return;

        Interlocked.Exchange(ref _lastDecodeTicks, now);
        global::Android.Hardware.Camera.Size? size;
        try
        {
            size = camera.GetParameters()?.PreviewSize;
        }
        catch
        {
            Interlocked.Exchange(ref _decoding, 0);
            return;
        }

        if (size is null)
        {
            Interlocked.Exchange(ref _decoding, 0);
            return;
        }

        byte[] frame = new byte[data.Length];
        Buffer.BlockCopy(data, 0, frame, 0, data.Length);
        int width = size.Width;
        int height = size.Height;

        Task.Run(() =>
        {
            try
            {
                string? decoded = DecodeFrame(frame, width, height);
                if (!string.IsNullOrWhiteSpace(decoded))
                    _preview?.Post(() => AcceptPayload(decoded));
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("FA.QR", "Camera decode failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _decoding, 0);
            }
        });
    }

    private LinearLayout CreateScannerPanel()
    {
        var panel = new LinearLayout(Context)
        {
            Orientation = Orientation.Vertical,
        };

        _previewFrame = new AspectRatioFrameLayout(Context)
        {
            Background = CreatePreviewBackground(),
        };
        _previewFrame.Configure(16.0 / 9.0, 16.0 / 9.0, centerCropContent: _isWideLayout);
        _previewFrame.SetPadding(Dp(1), Dp(1), Dp(1), Dp(1));

        _preview = new SurfaceView(Context);
        _preview.Holder?.AddCallback(this);
        _previewFrame.AddView(_preview, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        var scanFrame = new View(Context)
        {
            Background = CreateScanFrameBackground(),
        };
        int scanFrameSize = _isWideLayout ? Dp(150) : Dp(170);
        _previewFrame.AddView(scanFrame, new FrameLayout.LayoutParams(scanFrameSize, scanFrameSize, GravityFlags.Center));

        panel.AddView(_previewFrame, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        _status = new TextView(Context)
        {
            TextSize = 15.0f,
            Ellipsize = TextUtils.TruncateAt.End,
        };
        _status.SetSingleLine(true);
        _status.SetTextColor(ColorRes(Resource.Color.fa_text_secondary));
        _status.SetPadding(0, Dp(8), 0, Dp(8));
        panel.AddView(_status, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        var inputRow = new LinearLayout(Context)
        {
            Orientation = Orientation.Horizontal,
        };
        inputRow.SetGravity(GravityFlags.CenterVertical);

        _manualInput = new EditText(Context)
        {
            Hint = "FA1:PART_NUMBER",
            TextSize = 15.0f,
            Background = CreateInputBackground(),
        };
        _manualInput.SetSingleLine(true);
        _manualInput.SetTextColor(ColorRes(Resource.Color.fa_text_primary));
        _manualInput.SetHintTextColor(ColorRes(Resource.Color.fa_text_disabled));
        _manualInput.SetPadding(Dp(12), 0, Dp(12), 0);
        inputRow.AddView(_manualInput, new LinearLayout.LayoutParams(0, Dp(46), 1.0f)
        {
            RightMargin = Dp(8),
        });

        TextView submit = CreateCommandButton("Use", true);
        submit.Click += (_, _) =>
        {
            string? text = _manualInput?.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus("Type or paste a QR payload first.");
                return;
            }

            AcceptPayload(text);
        };
        inputRow.AddView(submit, new LinearLayout.LayoutParams(Dp(82), Dp(46))
        {
            RightMargin = Dp(8),
        });

        TextView cancel = CreateCommandButton("Cancel", true);
        cancel.Click += (_, _) => Dismiss();
        inputRow.AddView(cancel, new LinearLayout.LayoutParams(Dp(104), Dp(46)));

        panel.AddView(inputRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        return panel;
    }

    private LinearLayout CreateResultPanel()
    {
        var panel = new LinearLayout(Context)
        {
            Orientation = Orientation.Vertical,
        };

        _reviewStatus = new TextView(Context)
        {
            TextSize = 14.0f,
            Ellipsize = TextUtils.TruncateAt.End,
        };
        _reviewStatus.SetSingleLine(false);
        _reviewStatus.SetMaxLines(2);
        _reviewStatus.SetTextColor(ColorRes(Resource.Color.fa_text_secondary));
        _reviewStatus.SetPadding(0, 0, 0, Dp(12));
        panel.AddView(_reviewStatus, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        _lastScanValue = AddResultField(panel, "Last scan");
        _resolvedPartValue = AddResultField(panel, "Resolved part");

        TextView matchesLabel = CreateLabel("Matches");
        matchesLabel.SetPadding(0, Dp(4), 0, Dp(5));
        panel.AddView(matchesLabel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        var matchesScroll = new ScrollView(Context)
        {
            FillViewport = true,
            Background = CreateMatchesBackground(),
        };
        _matchesContainer = new LinearLayout(Context)
        {
            Orientation = Orientation.Vertical,
        };
        matchesScroll.AddView(_matchesContainer, new ScrollView.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        panel.AddView(matchesScroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            Dp(_isWideLayout ? 230 : 190)));

        var actions = new LinearLayout(Context)
        {
            Orientation = Orientation.Horizontal,
        };
        actions.SetPadding(0, Dp(12), 0, 0);

        _selectButton = CreateCommandButton("Select", false);
        _selectButton.Click += (_, _) => ExecuteForSelected(_selectMatch, "Selected");
        actions.AddView(_selectButton, WeightedButtonLayout());

        _isolateButton = CreateCommandButton("Isolate", false);
        _isolateButton.Click += (_, _) => ExecuteForSelected(_isolateMatch, "Isolated");
        actions.AddView(_isolateButton, WeightedButtonLayout());

        _isolateXrayButton = CreateCommandButton("X-Ray", false);
        _isolateXrayButton.Click += (_, _) => ExecuteForSelected(_isolateXrayMatch, "Isolated X-Ray");
        actions.AddView(_isolateXrayButton, WeightedButtonLayout(last: true));

        panel.AddView(actions, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        var footer = new LinearLayout(Context)
        {
            Orientation = Orientation.Horizontal,
        };
        footer.SetGravity(GravityFlags.Right);
        footer.SetPadding(0, Dp(10), 0, 0);

        _clearButton = CreateCommandButton("Scan again", false);
        _clearButton.Click += (_, _) => ClearResultAndResume();
        footer.AddView(_clearButton, new LinearLayout.LayoutParams(0, Dp(42), 1.0f)
        {
            RightMargin = Dp(8),
        });

        TextView close = CreateCommandButton("Close", true);
        close.Click += (_, _) => Dismiss();
        footer.AddView(close, new LinearLayout.LayoutParams(0, Dp(42), 1.0f));

        panel.AddView(footer, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        return panel;
    }

    private TextView AddResultField(LinearLayout parent, string label)
    {
        TextView labelView = CreateLabel(label);
        parent.AddView(labelView, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        var value = new TextView(Context)
        {
            Text = "-",
            TextSize = 15.0f,
            Ellipsize = TextUtils.TruncateAt.End,
        };
        value.SetSingleLine(false);
        value.SetMaxLines(2);
        value.SetTextColor(ColorRes(Resource.Color.fa_text_primary));
        value.SetPadding(0, Dp(3), 0, Dp(12));
        parent.AddView(value, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));
        return value;
    }

    private TextView CreateLabel(string text)
    {
        var view = new TextView(Context)
        {
            Text = text,
            TextSize = 12.5f,
        };
        view.SetIncludeFontPadding(false);
        view.SetTextColor(ColorRes(Resource.Color.fa_text_secondary));
        return view;
    }

    private LinearLayout.LayoutParams WeightedButtonLayout(bool last = false)
        => new(0, Dp(42), 1.0f)
        {
            RightMargin = last ? 0 : Dp(8),
        };

    private string? DecodeFrame(byte[] frame, int width, int height)
    {
        var source = new PlanarYUVLuminanceSource(frame, width, height, 0, 0, width, height, false);
        return _reader.Decode(source)?.Text;
    }

    private void StartCamera()
    {
        if (_camera is not null || _cameraPausedForResult || !_surfaceReady || _preview?.Holder is null)
            return;

        try
        {
            int cameraId = ChooseCameraId();
            global::Android.Hardware.Camera? openedCamera = global::Android.Hardware.Camera.Open(cameraId);
            if (openedCamera is null)
                throw new InvalidOperationException("Camera open returned null.");

            _camera = openedCamera;
            var parameters = openedCamera.GetParameters();
            int displayOrientation = ResolveCameraDisplayOrientation(cameraId);
            if (parameters is not null)
            {
                var bestSize = ChoosePreviewSize(parameters.SupportedPreviewSizes);
                if (bestSize is not null)
                {
                    parameters.SetPreviewSize(bestSize.Width, bestSize.Height);
                    UpdatePreviewAspect(bestSize.Width, bestSize.Height, displayOrientation);
                }

                if (parameters.SupportedFocusModes?.Contains(global::Android.Hardware.Camera.Parameters.FocusModeContinuousPicture) == true)
                    parameters.FocusMode = global::Android.Hardware.Camera.Parameters.FocusModeContinuousPicture;
                else if (parameters.SupportedFocusModes?.Contains(global::Android.Hardware.Camera.Parameters.FocusModeAuto) == true)
                    parameters.FocusMode = global::Android.Hardware.Camera.Parameters.FocusModeAuto;

                parameters.PreviewFormat = ImageFormatType.Nv21;
                openedCamera.SetParameters(parameters);
            }

            openedCamera.SetDisplayOrientation(displayOrientation);
            openedCamera.SetPreviewDisplay(_preview.Holder);
            openedCamera.SetPreviewCallback(this);
            openedCamera.StartPreview();
            SetStatus("Scanning...");
            global::Android.Util.Log.Info("FA.QR", "Camera scanner started.");
        }
        catch (Exception ex)
        {
            SetStatus("Camera unavailable. Type or paste the QR payload below.");
            global::Android.Util.Log.Warn("FA.QR", "Could not start QR camera: " + ex.Message);
            StopCamera();
        }
    }

    private void StopCamera()
    {
        global::Android.Hardware.Camera? camera = _camera;
        _camera = null;
        if (camera is null)
            return;

        try { camera.SetPreviewCallback(null); } catch { }
        try { camera.StopPreview(); } catch { }
        try { camera.Release(); } catch { }
        global::Android.Util.Log.Info("FA.QR", "Camera scanner stopped.");
    }

    private void UpdatePreviewAspect(int previewWidth, int previewHeight, int displayOrientation)
    {
        if (_previewFrame is null || previewWidth <= 0 || previewHeight <= 0)
            return;

        double aspect = displayOrientation is 90 or 270
            ? (double)previewHeight / previewWidth
            : (double)previewWidth / previewHeight;
        double viewportAspect = _isWideLayout ? 16.0 / 9.0 : aspect;
        _previewFrame.Configure(viewportAspect, aspect, centerCropContent: _isWideLayout);
    }

    private void AcceptPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return;

        _cameraPausedForResult = true;
        StopCamera();
        _manualInput?.ClearFocus();

        _currentResult = _resolvePayload(payload);
        _selectedMatchIndex = _currentResult.Matches.Count > 0 ? 0 : -1;
        RenderResult();
        global::Android.Util.Log.Info(
            "FA.QR",
            $"Scan surfaced result: payload='{_currentResult.LastScan}', part='{_currentResult.ResolvedPartNumber}', matches={_currentResult.Matches.Count}.");
    }

    private void ClearResultAndResume()
    {
        if (_manualInput is not null)
            _manualInput.Text = string.Empty;
        _currentResult = AndroidQrScanResult.Empty("Point the camera at a Fabrication Assistant QR code.");
        _selectedMatchIndex = -1;
        _cameraPausedForResult = false;
        RenderResult();
        if (_surfaceReady)
            StartCamera();
    }

    private void RenderResult()
    {
        bool showReview = _cameraPausedForResult || !string.IsNullOrWhiteSpace(_currentResult.LastScan);
        if (_scanStage is not null)
            _scanStage.Visibility = showReview ? ViewStates.Gone : ViewStates.Visible;
        if (_reviewStage is not null)
            _reviewStage.Visibility = showReview ? ViewStates.Visible : ViewStates.Gone;

        if (_lastScanValue is not null)
            _lastScanValue.Text = string.IsNullOrWhiteSpace(_currentResult.LastScan) ? "-" : _currentResult.LastScan;
        if (_resolvedPartValue is not null)
            _resolvedPartValue.Text = string.IsNullOrWhiteSpace(_currentResult.ResolvedPartNumber) ? "-" : _currentResult.ResolvedPartNumber;

        SetStatus(_currentResult.StatusMessage);
        RenderMatches();
        UpdateActionButtons();
    }

    private void RenderMatches()
    {
        if (_matchesContainer is null)
            return;

        _matchesContainer.RemoveAllViews();
        if (_currentResult.Matches.Count == 0)
        {
        var empty = new TextView(Context)
        {
            Text = string.IsNullOrWhiteSpace(_currentResult.LastScan) ? "No scan yet." : "No matching parts.",
            TextSize = 13.5f,
            Gravity = GravityFlags.CenterVertical,
        };
            empty.SetTextColor(ColorRes(Resource.Color.fa_text_disabled));
            empty.SetPadding(Dp(10), Dp(12), Dp(10), Dp(12));
            _matchesContainer.AddView(empty, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent));
            return;
        }

        for (int i = 0; i < _currentResult.Matches.Count; i++)
            AddMatchRow(i, _currentResult.Matches[i]);
    }

    private void AddMatchRow(int index, AndroidQrScanMatch match)
    {
        if (_matchesContainer is null)
            return;

        bool selected = index == _selectedMatchIndex;
        var row = new LinearLayout(Context)
        {
            Orientation = Orientation.Horizontal,
            Clickable = true,
            Focusable = true,
            Background = CreateMatchRowBackground(selected),
        };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(8), Dp(7), Dp(8), Dp(7));
        row.Click += (_, _) =>
        {
            _selectedMatchIndex = index;
            RenderMatches();
            UpdateActionButtons();
        };

        var id = new TextView(Context)
        {
            Text = match.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TextSize = 13.0f,
            Gravity = GravityFlags.CenterVertical,
        };
        id.SetIncludeFontPadding(false);
        id.SetTextColor(ColorRes(Resource.Color.fa_text_disabled));
        row.AddView(id, new LinearLayout.LayoutParams(Dp(42), ViewGroup.LayoutParams.MatchParent));

        var texts = new LinearLayout(Context)
        {
            Orientation = Orientation.Vertical,
        };

        var title = new TextView(Context)
        {
            Text = match.DisplayName,
            TextSize = 14.5f,
            Ellipsize = TextUtils.TruncateAt.End,
        };
        title.SetIncludeFontPadding(false);
        title.SetSingleLine(true);
        title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        title.SetTextColor(ColorRes(Resource.Color.fa_text_primary));
        texts.AddView(title, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        var path = new TextView(Context)
        {
            Text = match.Path,
            TextSize = 12.0f,
            Ellipsize = TextUtils.TruncateAt.End,
        };
        path.SetIncludeFontPadding(false);
        path.SetSingleLine(true);
        path.SetTextColor(ColorRes(Resource.Color.fa_text_secondary));
        texts.AddView(path, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        row.AddView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f));
        _matchesContainer.AddView(row, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            Dp(58))
        {
            BottomMargin = Dp(2),
        });
    }

    private void UpdateActionButtons()
    {
        bool hasSelection = TryGetSelectedMatch(out _);
        SetCommandEnabled(_selectButton, hasSelection);
        SetCommandEnabled(_isolateButton, hasSelection);
        SetCommandEnabled(_isolateXrayButton, hasSelection);
        SetCommandEnabled(_clearButton, !string.IsNullOrWhiteSpace(_currentResult.LastScan) || _currentResult.Matches.Count > 0);
    }

    private bool TryGetSelectedMatch(out AndroidQrScanMatch match)
    {
        if (_selectedMatchIndex >= 0 && _selectedMatchIndex < _currentResult.Matches.Count)
        {
            match = _currentResult.Matches[_selectedMatchIndex];
            return true;
        }

        match = default;
        return false;
    }

    private void ExecuteForSelected(Action<int> action, string verb)
    {
        if (!TryGetSelectedMatch(out AndroidQrScanMatch match))
            return;

        try
        {
            action(match.NodeId);
            SetStatus($"{verb}: {match.DisplayName}");
        }
        catch (Exception ex)
        {
            SetStatus("Action failed: " + ex.Message);
            global::Android.Util.Log.Warn("FA.QR", $"Action '{verb}' failed for nodeId={match.NodeId}: {ex.Message}");
        }
    }

    private void SetStatus(string text)
    {
        if (_status is not null)
            _status.Text = text;
        if (_reviewStatus is not null)
            _reviewStatus.Text = text;
    }

    private TextView CreateCommandButton(string text, bool enabled)
    {
        var button = new TextView(Context)
        {
            Text = text,
            TextSize = 14.5f,
            Gravity = GravityFlags.Center,
            Clickable = true,
            Focusable = true,
            Enabled = enabled,
            Background = CreateButtonBackground(accent: false),
        };
        button.SetSingleLine(true);
        button.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        button.SetTextColor(ColorRes(Resource.Color.fa_accent_500));
        button.SetPadding(Dp(8), 0, Dp(8), 0);
        SetCommandEnabled(button, enabled);
        return button;
    }

    private void SetCommandEnabled(TextView? button, bool enabled)
    {
        if (button is null)
            return;

        button.Enabled = enabled;
        button.Alpha = enabled ? 1.0f : 0.42f;
    }

    private GradientDrawable CreateDialogBackground()
    {
        var background = new GradientDrawable();
        background.SetShape(ShapeType.Rectangle);
        background.SetColor(ColorRes(Resource.Color.fa_surface_background));
        background.SetCornerRadius(Dp(18));
        background.SetStroke(Dp(1), ColorRes(Resource.Color.fa_border));
        return background;
    }

    private GradientDrawable CreatePreviewBackground()
    {
        var background = new GradientDrawable();
        background.SetShape(ShapeType.Rectangle);
        background.SetColor(ColorRes(Resource.Color.fa_app_background));
        background.SetCornerRadius(Dp(6));
        background.SetStroke(Dp(1), ColorRes(Resource.Color.fa_border));
        return background;
    }

    private GradientDrawable CreateScanFrameBackground()
    {
        var background = new GradientDrawable();
        background.SetShape(ShapeType.Rectangle);
        background.SetColor(Color.Transparent);
        background.SetCornerRadius(Dp(4));
        background.SetStroke(Dp(1), Color.Argb(180, 45, 212, 191));
        return background;
    }

    private GradientDrawable CreateInputBackground()
    {
        var background = new GradientDrawable();
        background.SetShape(ShapeType.Rectangle);
        background.SetColor(ColorRes(Resource.Color.fa_app_background));
        background.SetCornerRadius(Dp(6));
        background.SetStroke(Dp(1), ColorRes(Resource.Color.fa_border));
        return background;
    }

    private GradientDrawable CreateMatchesBackground()
    {
        var background = new GradientDrawable();
        background.SetShape(ShapeType.Rectangle);
        background.SetColor(Color.Argb(178, 20, 21, 24));
        background.SetCornerRadius(Dp(5));
        background.SetStroke(Dp(1), ColorRes(Resource.Color.fa_border));
        return background;
    }

    private Drawable CreateMatchRowBackground(bool selected)
    {
        if (selected)
        {
            var background = new GradientDrawable();
            background.SetShape(ShapeType.Rectangle);
            background.SetColor(Color.Argb(202, 13, 61, 56));
            background.SetCornerRadius(Dp(3));
            background.SetStroke(Dp(1), Color.Argb(180, 20, 184, 166));
            return background;
        }

        var states = new StateListDrawable();
        states.AddState(new[] { AndroidStatePressed }, CreateMatchRowHoverBackground());
        states.AddState(new[] { AndroidStateHovered }, CreateMatchRowHoverBackground());
        states.AddState(new[] { AndroidStateFocused }, CreateMatchRowHoverBackground());
        states.AddState(Array.Empty<int>(), new ColorDrawable(Color.Transparent));
        return states;
    }

    private GradientDrawable CreateMatchRowHoverBackground()
    {
        var fill = new GradientDrawable();
        fill.SetShape(ShapeType.Rectangle);
        fill.SetColor(Color.Argb(54, 45, 212, 191));
        fill.SetCornerRadius(Dp(3));
        return fill;
    }

    private Drawable CreateButtonBackground(bool accent)
    {
        var states = new StateListDrawable();
        states.AddState(new[] { -AndroidStateEnabled }, CreateButtonFill(Color.Argb(80, 44, 45, 50), Color.Argb(80, 53, 54, 61), accent));
        states.AddState(new[] { AndroidStatePressed }, CreateButtonFill(Color.Argb(82, 45, 212, 191), ColorRes(Resource.Color.fa_accent_600), accent));
        states.AddState(new[] { AndroidStateHovered }, CreateButtonFill(Color.Argb(52, 45, 212, 191), ColorRes(Resource.Color.fa_accent_500), accent));
        states.AddState(new[] { AndroidStateFocused }, CreateButtonFill(Color.Argb(52, 45, 212, 191), ColorRes(Resource.Color.fa_accent_500), accent));
        states.AddState(Array.Empty<int>(), CreateButtonFill(ColorRes(Resource.Color.fa_control_background), ColorRes(Resource.Color.fa_border), accent));
        return states;
    }

    private GradientDrawable CreateButtonFill(Color color, Color stroke, bool accent)
    {
        var fill = new GradientDrawable();
        fill.SetShape(ShapeType.Rectangle);
        fill.SetColor(accent ? ColorRes(Resource.Color.fa_accent_700) : color);
        fill.SetCornerRadius(Dp(6));
        fill.SetStroke(Dp(1), stroke);
        return fill;
    }

    private Color ColorRes(int resourceId)
        => new(global::AndroidX.Core.Content.ContextCompat.GetColor(Context, resourceId));

    private int Dp(float dp)
        => (int)Math.Round(dp * (Context.Resources?.DisplayMetrics?.Density ?? 1.0f));

    private static int ChooseCameraId()
    {
        int count = global::Android.Hardware.Camera.NumberOfCameras;
        var info = new global::Android.Hardware.Camera.CameraInfo();
        for (int i = 0; i < count; i++)
        {
            global::Android.Hardware.Camera.GetCameraInfo(i, info);
            if (info.Facing == global::Android.Hardware.CameraFacing.Back)
                return i;
        }

        return 0;
    }

    private int ResolveCameraDisplayOrientation(int cameraId)
    {
        var info = new global::Android.Hardware.Camera.CameraInfo();
        global::Android.Hardware.Camera.GetCameraInfo(cameraId, info);

        int degrees = GetDisplayRotationDegrees();
        int orientation;
        if (info.Facing == global::Android.Hardware.CameraFacing.Front)
            orientation = (360 - ((info.Orientation + degrees) % 360)) % 360;
        else
            orientation = (info.Orientation - degrees + 360) % 360;

        if (_isWideLayout)
            orientation = (orientation + 270) % 360;

        global::Android.Util.Log.Info("FA.QR", $"Camera orientation camera={cameraId} sensor={info.Orientation} display={degrees} applied={orientation} wide={_isWideLayout}");
        return orientation;
    }

    private int GetDisplayRotationDegrees()
    {
        SurfaceOrientation rotation = SurfaceOrientation.Rotation0;
        if (Context is Activity activity)
            rotation = activity.WindowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0;

        return rotation switch
        {
            SurfaceOrientation.Rotation90 => 90,
            SurfaceOrientation.Rotation180 => 180,
            SurfaceOrientation.Rotation270 => 270,
            _ => 0,
        };
    }

    private static global::Android.Hardware.Camera.Size? ChoosePreviewSize(IList<global::Android.Hardware.Camera.Size>? sizes)
    {
        if (sizes is null || sizes.Count == 0)
            return null;

        return sizes
            .OrderBy(size => Math.Abs((size.Width * size.Height) - (1280 * 720)))
            .ThenBy(size => size.Width)
            .FirstOrDefault();
    }

    private sealed class AspectRatioFrameLayout : FrameLayout
    {
        private double _viewportAspect = 16.0 / 9.0;
        private double _contentAspect = 16.0 / 9.0;
        private bool _centerCropContent;

        public AspectRatioFrameLayout(Context context)
            : base(context)
        {
            SetClipChildren(true);
            SetClipToPadding(true);
        }

        public void Configure(double viewportAspect, double contentAspect, bool centerCropContent)
        {
            if (!IsValidAspect(viewportAspect) || !IsValidAspect(contentAspect))
                return;

            if (Math.Abs(_viewportAspect - viewportAspect) < 0.0001
                && Math.Abs(_contentAspect - contentAspect) < 0.0001
                && _centerCropContent == centerCropContent)
                return;

            _viewportAspect = viewportAspect;
            _contentAspect = contentAspect;
            _centerCropContent = centerCropContent;
            RequestLayout();
        }

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int width = MeasureSpec.GetSize(widthMeasureSpec);
            if (width <= 0)
            {
                base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
                return;
            }

            int height = Math.Max(1, (int)Math.Round(width / _viewportAspect));
            int exactHeight = MeasureSpec.MakeMeasureSpec(height, MeasureSpecMode.Exactly);
            base.OnMeasure(widthMeasureSpec, exactHeight);
        }

        protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
        {
            base.OnLayout(changed, left, top, right, bottom);
            if (ChildCount == 0)
                return;

            var preview = GetChildAt(0);
            if (preview is null || !IsValidAspect(_contentAspect))
                return;

            int contentLeft = PaddingLeft;
            int contentTop = PaddingTop;
            int contentWidth = Math.Max(1, right - left - PaddingLeft - PaddingRight);
            int contentHeight = Math.Max(1, bottom - top - PaddingTop - PaddingBottom);
            double containerAspect = (double)contentWidth / contentHeight;

            int previewWidth;
            int previewHeight;
            if (_centerCropContent)
            {
                if (_contentAspect > containerAspect)
                {
                    previewHeight = contentHeight;
                    previewWidth = Math.Max(1, (int)Math.Ceiling(previewHeight * _contentAspect));
                }
                else
                {
                    previewWidth = contentWidth;
                    previewHeight = Math.Max(1, (int)Math.Ceiling(previewWidth / _contentAspect));
                }
            }
            else if (_contentAspect > containerAspect)
            {
                previewWidth = contentWidth;
                previewHeight = Math.Max(1, (int)Math.Round(previewWidth / _contentAspect));
            }
            else
            {
                previewHeight = contentHeight;
                previewWidth = Math.Max(1, (int)Math.Round(previewHeight * _contentAspect));
            }

            int previewLeft = contentLeft + (contentWidth - previewWidth) / 2;
            int previewTop = contentTop + (contentHeight - previewHeight) / 2;
            preview.Layout(previewLeft, previewTop, previewLeft + previewWidth, previewTop + previewHeight);
        }

        private static bool IsValidAspect(double aspect)
            => double.IsFinite(aspect) && aspect > 0.0;
    }
}

internal readonly record struct AndroidQrScanMatch(int NodeId, string DisplayName, string Path);

internal sealed record AndroidQrScanResult(
    string LastScan,
    string ResolvedPartNumber,
    string StatusMessage,
    IReadOnlyList<AndroidQrScanMatch> Matches)
{
    public static AndroidQrScanResult Empty(string statusMessage)
        => new(string.Empty, string.Empty, statusMessage, Array.Empty<AndroidQrScanMatch>());
}
#pragma warning restore CS0618
