using System.Collections.ObjectModel;
using System.Text.Json;
using MandoCode.Models;
using MandoCode.Desktop.Services;
using MandoCode.Desktop.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MandoCode.Desktop;

public sealed partial class ChatTabView
{
    // ============================================================
    // IApprovalUi — this tab's approval overlay (completes the harness's awaited TCS)
    // ============================================================

    public Task<string> ShowApprovalAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvalTcs = tcs;

        var reg = ct.CanBeCanceled
            ? ct.Register(() =>
            {
                tcs.TrySetCanceled(ct);
                OnUi(() => { HideApprovalOverlay(); HidePlanApprovalBar(); });
            })
            : default(CancellationTokenRegistration);

        OnUi(() =>
        {
            // What the cross-tab toast will say — a specific "what's waiting" line, not the modal's
            // question. Set for both the bottom-bar and modal paths.
            _approvalSummary = string.IsNullOrEmpty(request.ToastSummary) ? request.Title : request.ToastSummary;

            // Plan approvals render as a non-covering bottom bar so the plan card stays readable.
            if (request.BottomBar)
            {
                ShowPlanApprovalBar(request, choice =>
                {
                    HidePlanApprovalBar();
                    reg.Dispose();
                    tcs.TrySetResult(choice);
                    if (ReferenceEquals(_approvalTcs, tcs)) _approvalTcs = null;
                });
                return;
            }

            ApprovalTitle.Text = request.Title;

            ApprovalSubtitle.Text = request.Subtitle ?? "";
            ApprovalSubtitle.Visibility = string.IsNullOrEmpty(request.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

            ApprovalDetail.Text = request.Detail ?? "";
            ApprovalDetail.Visibility = string.IsNullOrEmpty(request.Detail) ? Visibility.Collapsed : Visibility.Visible;

            // Pull the shared, theme-mutated brushes from app resources so the approval diff
            // follows the active theme (these used to be hardcoded LightSkyBlue/red/gray, which
            // stayed blue under every theme — jarring under E-Ink). Mirrors the transcript's
            // diff coloring: command/added -> sky, removed -> red, context -> dim.
            var skyBrush = (SolidColorBrush)Application.Current.Resources["MandoSkyBrush"];
            var redBrush = (SolidColorBrush)Application.Current.Resources["MandoRedBrush"];
            var dimBrush = (SolidColorBrush)Application.Current.Resources["MandoDimBrush"];

            var rows = new List<DiffLineVm>();
            if (request.CommandText != null)
            {
                rows.Add(new DiffLineVm
                {
                    Text = $"$ {request.CommandText}",
                    Brush = skyBrush
                });
            }
            if (request.DiffLines != null)
            {
                foreach (var line in request.DiffLines)
                {
                    var (prefix, brush) = line.LineType switch
                    {
                        DiffLineType.Added => ("+ ", skyBrush),
                        DiffLineType.Removed => ("- ", redBrush),
                        _ => ("  ", dimBrush)
                    };
                    var num = (line.LineType == DiffLineType.Added ? line.NewLineNumber : line.OldLineNumber);
                    rows.Add(new DiffLineVm
                    {
                        Text = $"{(num.HasValue ? num.Value.ToString().PadLeft(4) : "    ")} {prefix}{line.Content}",
                        Brush = brush
                    });
                }
                if (request.DiffSummary != null)
                    rows.Add(new DiffLineVm { Text = "", Brush = dimBrush });
            }
            ApprovalDiffList.ItemsSource = rows;
            ApprovalBodyScroll.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (!string.IsNullOrEmpty(request.DiffSummary))
            {
                ApprovalDetail.Text = request.DiffSummary;
                ApprovalDetail.Visibility = Visibility.Visible;
            }

            ApprovalButtons.Children.Clear();
            foreach (var option in request.Options)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                if (!string.IsNullOrEmpty(option.Glyph))
                    content.Children.Add(new FontIcon { Glyph = option.Glyph, FontSize = 13 });
                content.Children.Add(new TextBlock { Text = option.Label });
                var button = new Button
                {
                    Content = content,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Tag = option.Label
                };
                button.Foreground = option.Kind switch
                {
                    ApprovalOptionKind.Proceed => (SolidColorBrush)Application.Current.Resources["MandoGreenBrush"],
                    ApprovalOptionKind.Destructive => (SolidColorBrush)Application.Current.Resources["MandoRedBrush"],
                    _ => (SolidColorBrush)Application.Current.Resources["MandoGoldBrush"]
                };
                if (!string.IsNullOrEmpty(option.Description))
                    ToolTipService.SetToolTip(button, option.Description);
                button.Click += (_, _) =>
                {
                    var choice = (string)button.Tag;
                    HideApprovalOverlay();
                    reg.Dispose();
                    _approvalTcs?.TrySetResult(choice);
                    _approvalTcs = null;
                };
                ApprovalButtons.Children.Add(button);
            }

            InstructionPanel.Visibility = Visibility.Collapsed;
            ApprovalButtons.Visibility = Visibility.Visible;
            SetApprovalCardSize(instructionMode: false);
            ShowApprovalOverlay();
        });

        return tcs.Task;
    }

    /// <summary>Approval mode: compact centered card. Instruction mode: full width and
    /// half the window height, centered — room to write real instructions.</summary>
    private void SetApprovalCardSize(bool instructionMode)
    {
        if (instructionMode)
        {
            ApprovalCard.HorizontalAlignment = HorizontalAlignment.Stretch;
            ApprovalCard.MaxWidth = double.PositiveInfinity;
            ApprovalCard.MaxHeight = double.PositiveInfinity;
            ApprovalCard.Height = Math.Max(320, ChatRoot.ActualHeight * 0.5);
        }
        else
        {
            ApprovalCard.HorizontalAlignment = HorizontalAlignment.Center;
            ApprovalCard.MaxWidth = 860;
            ApprovalCard.MaxHeight = 640;
            ApprovalCard.Height = double.NaN;
        }
    }

    public Task<string> ShowInstructionInputAsync(string prompt, string placeholder = "", bool allowCancel = false, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _instructionTcs = tcs;

        OnUi(() =>
        {
            // First line is the question; any extra lines (e.g. a validation error on
            // re-prompt) render below it in the smaller prompt text.
            var newline = prompt.IndexOf('\n');
            ApprovalTitle.Text = newline < 0 ? prompt : prompt[..newline];
            InstructionPrompt.Text = newline < 0 ? "" : prompt[(newline + 1)..].Trim();
            InstructionPrompt.Visibility = InstructionPrompt.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            ApprovalSubtitle.Visibility = Visibility.Collapsed;
            ApprovalDetail.Visibility = Visibility.Collapsed;
            ApprovalBodyScroll.Visibility = Visibility.Collapsed;
            ApprovalButtons.Visibility = Visibility.Collapsed;

            InstructionBox.Text = "";
            InstructionBox.PlaceholderText = string.IsNullOrEmpty(placeholder)
                ? "Type your answer and press Enter"
                : placeholder;
            InstructionCancelButton.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
            InstructionPanel.Visibility = Visibility.Visible;
            SetApprovalCardSize(instructionMode: true);
            ShowApprovalOverlay();
            InstructionBox.Focus(FocusState.Programmatic);
        });

        return tcs.Task;
    }

    private void InstructionBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            // Shift+Enter inserts a newline (the box is multi-line); plain Enter submits.
            // PreviewKeyDown is required here — with AcceptsReturn, the class handler
            // would insert the newline before a plain KeyDown handler ever ran.
            var shift = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shift) return;
            e.Handled = true;
            SubmitInstruction();
        }
        else if (e.Key == VirtualKey.Escape && InstructionCancelButton.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            CancelInstruction();
        }
    }

    private void InstructionSubmit_Click(object sender, RoutedEventArgs e) => SubmitInstruction();

    private void InstructionCancel_Click(object sender, RoutedEventArgs e) => CancelInstruction();

    private void SubmitInstruction()
    {
        var text = InstructionBox.Text;
        HideApprovalOverlay();
        _instructionTcs?.TrySetResult(text);
        _instructionTcs = null;
    }

    private void CancelInstruction()
    {
        HideApprovalOverlay();
        _instructionTcs?.TrySetResult(ApprovalSignals.Cancelled);
        _instructionTcs = null;
    }

    /// <summary>An approval raised in a background tab can't steal focus, so MainWindow badges
    /// that tab and raises the toast instead.</summary>
    private void ShowApprovalOverlay()
    {
        ApprovalOverlay.Visibility = Visibility.Visible;
        ApprovalStateChanged?.Invoke(this);
    }

    private void HideApprovalOverlay()
    {
        ApprovalOverlay.Visibility = Visibility.Collapsed;
        ApprovalDiffList.ItemsSource = null;
        ApprovalStateChanged?.Invoke(this);
        InputBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Slides the plan-approval bar up above the input. Unlike the modal it doesn't cover the
    /// transcript (the plan stays readable), but it DOES gate input — the turn is awaiting the choice.</summary>
    private void ShowPlanApprovalBar(ApprovalRequest request, Action<string> onChosen)
    {
        // Windows 98 theme: the bar drops its rounded card look and reads as a silver
        // dialog strip — square corners, dialog-face background. Rebuilt on every show,
        // so live theme switches take effect on the next approval.
        var win98 = ThemeManager.Current.Win98;
        PlanApprovalBar.CornerRadius = new CornerRadius(win98 ? 0 : 12);
        PlanApprovalBar.Background = (Brush)Application.Current.Resources[
            win98 ? "MandoBackgroundBrush" : "MandoPanelBrush"];

        PlanApprovalTitle.Text = request.Title;

        // Command approvals ride this bar too: show the command in monospace. The buttons
        // live in a WrapPanel — one horizontal row whenever it fits, wrapping only when the
        // window is too narrow for the long "don't ask again" labels.
        PlanApprovalCommand.Text = string.IsNullOrEmpty(request.CommandText) ? "" : "$ " + request.CommandText;
        PlanApprovalCommand.Visibility = string.IsNullOrEmpty(request.CommandText)
            ? Visibility.Collapsed : Visibility.Visible;

        PlanApprovalButtons.Children.Clear();
        foreach (var option in request.Options)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (!string.IsNullOrEmpty(option.Glyph))
                content.Children.Add(new FontIcon { Glyph = option.Glyph, FontSize = 13 });
            content.Children.Add(new TextBlock { Text = option.Label });

            var button = new Button { Content = content, Tag = option.Label, Padding = new Thickness(14, 6, 14, 6) };
            if (win98) button.CornerRadius = new CornerRadius(0);   // square, like every 98 control
            if (option.Kind == ApprovalOptionKind.Proceed)
                button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];   // primary
            else
                button.Foreground = option.Kind == ApprovalOptionKind.Destructive
                    ? (SolidColorBrush)Application.Current.Resources["MandoRedBrush"]
                    : (SolidColorBrush)Application.Current.Resources["MandoGoldBrush"];
            if (!string.IsNullOrEmpty(option.Description))
                ToolTipService.SetToolTip(button, option.Description);
            button.Click += (_, _) => onChosen((string)button.Tag);
            PlanApprovalButtons.Children.Add(button);
        }

        // Gate input while the plan is awaiting a decision.
        InputBox.IsEnabled = false;
        SendButton.IsEnabled = false;
        EmojiButton.IsEnabled = false;

        PlanApprovalBar.Visibility = Visibility.Visible;
        ApprovalStateChanged?.Invoke(this);

        var slide = new DoubleAnimation
        {
            From = 24, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, PlanApprovalTransform);
        Storyboard.SetTargetProperty(slide, "Y");
        var fade = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
        };
        Storyboard.SetTarget(fade, PlanApprovalBar);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(slide);
        sb.Children.Add(fade);
        sb.Begin();
    }

    private void HidePlanApprovalBar()
    {
        if (PlanApprovalBar.Visibility != Visibility.Visible) return;
        PlanApprovalBar.Visibility = Visibility.Collapsed;
        InputBox.IsEnabled = true;
        SendButton.IsEnabled = true;
        EmojiButton.IsEnabled = true;
        ApprovalStateChanged?.Invoke(this);
        InputBox.Focus(FocusState.Programmatic);
    }
}
