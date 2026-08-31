using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class MainPage
{
    private async void HoyoLabSyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PublisherAccountService.HoyoLabManualSyncAvailable || publisherAccountActionInFlight
            || GameSelector?.SelectedItem is not GameLauncherItem { Id: "hsr" }) return;
        try
        {
            await ShowHoyoLabSyncAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            HeroDescription.Text = "Sync controls could not open. No new sync was requested.";
        }
    }

    private async Task ShowHoyoLabSyncAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            pageLease?.CancellationToken ?? CancellationToken.None);
        var token = cancellation.Token;
        var open = true;
        var busy = false;
        var contextInvalidated = false;
        var syncSlot = publisherAccounts.HoyoLabAccounts.ActiveSlotId;
        var displayedSlot = syncSlot;
        var displayedConsent = HasPublisherConsent("hsr");
        var consentWhenOpened = displayedConsent;
        HoyoLabSyncSummary summary = new(false, false, 0, null);
        HoyoLabGameBundle? bundle = null;
        Func<CancellationToken, Task<HoyoLabManualSyncResult>>? confirmedAction = null;

        TextBlock Note(string text) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = (FontFamily)Application.Current.Resources["NyxBodyFont"],
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["MoonBrush"],
        };
        var status = Note("Reading saved sync status…");
        AutomationProperties.SetName(status, "Star Rail sync status");
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var message = Note(string.Empty);
        AutomationProperties.SetLiveSetting(message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var recoveryCode = new TextBox
        {
            Header = "Recovery code",
            PlaceholderText = "Generate a code, or enter your existing NYX-HOYO code",
            MaxLength = 128,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(recoveryCode, "Private HoYo recovery code; keep it safe");
        var optIn = new CheckBox
        {
            Content = Note("I want to save an encrypted Star Rail copy on Pengo. Sync only when I ask."),
        };
        var generate = CreateHoyoLabManagerButton("Generate code", "Generate a private HoYo recovery code");
        var connect = CreateHoyoLabManagerButton("Enable & sync", "Enable encrypted manual Star Rail sync");
        var syncNow = CreateHoyoLabManagerButton("Sync now", "Sync Star Rail now");
        var rotate = CreateHoyoLabManagerButton("Change code…", "Review changing the HoYo recovery code");
        var stop = CreateHoyoLabManagerButton("Stop syncing here", "Forget only this PC's sync key; keep local and cloud data");
        var retry = CreateHoyoLabManagerButton("Retry deletion", "Retry already requested HoYo deletions");
        var website = CreateHoyoLabManagerButton("Open My HoYo", "Open My HoYo on Pengo; no recovery code is sent in the link");
        var scope = new ComboBox
        {
            Header = "Remove saved data",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[]
            {
                "One Star Rail role",
                "Star Rail cloud copy",
                "All HoYo data & this PC",
                "This PC only",
            },
            SelectedIndex = 0,
        };
        AutomationProperties.SetName(scope, "HoYo data removal scope");
        var roles = new ComboBox { Header = "Star Rail role", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(roles, "Exact Star Rail role to remove");
        var remove = CreateHoyoLabManagerButton("Review removal…", "Review exactly which saved HoYo data will be removed");
        var confirmation = Note(string.Empty);
        var confirm = CreateHoyoLabManagerButton("Confirm", "Confirm the described action");
        var cancel = CreateHoyoLabManagerButton("Cancel", "Cancel this action without changing data");
        var confirmationPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        confirmationPanel.Children.Add(confirmation);
        var confirmationButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        confirmationButtons.Children.Add(confirm);
        confirmationButtons.Children.Add(cancel);
        confirmationPanel.Children.Add(confirmationButtons);

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(status);
        content.Children.Add(Note("Only remembered resources and completed achievements are shared. Other games and pull history are unchanged."));
        content.Children.Add(recoveryCode);
        content.Children.Add(Note("Keep the code somewhere safe. Losing it and every remembered device makes the cloud copy unrecoverable. Nyx cannot show the code again after this window closes."));
        content.Children.Add(optIn);
        var setup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        setup.Children.Add(generate);
        setup.Children.Add(connect);
        content.Children.Add(setup);
        var syncActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        syncActions.Children.Add(syncNow);
        syncActions.Children.Add(rotate);
        content.Children.Add(syncActions);
        content.Children.Add(stop);
        content.Children.Add(website);
        content.Children.Add(scope);
        content.Children.Add(roles);
        content.Children.Add(remove);
        content.Children.Add(confirmationPanel);
        content.Children.Add(retry);
        content.Children.Add(message);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Star Rail · Sync & My HoYo",
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = Math.Clamp(ActualHeight - 180, 180, 640),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            Background = (Brush)Application.Current.Resources["SettingsSurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["DeckBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.None,
            CloseButtonStyle = (Style)Application.Current.Resources["NyxDialogQuietStyle"],
        };
        ApplyNyxAccentResources(dialog.Resources);

        void ClearConfirmation()
        {
            confirmedAction = null;
            confirmation.Text = string.Empty;
            confirmationPanel.Visibility = Visibility.Collapsed;
        }

        void InvalidateContext()
        {
            contextInvalidated = true;
            displayedSlot = null;
            displayedConsent = false;
            summary = summary with { Enabled = false, LastSyncedAt = null };
            recoveryCode.Text = string.Empty;
            optIn.IsChecked = false;
            bundle = null;
            roles.Items.Clear();
            ClearConfirmation();
            status.Text = "The account changed. Reopen this window before starting a new account action.";
            message.Text = "Existing deletion requests can still be retried here.";
            Render();
        }

        void Render()
        {
            var ready = open && !busy && confirmedAction is null;
            var local = ready && !contextInvalidated && displayedSlot is not null
                && displayedSlot == syncSlot && displayedConsent;
            generate.IsEnabled = local && bundle is not null && summary.Available && !summary.Enabled;
            optIn.IsEnabled = generate.IsEnabled;
            recoveryCode.IsReadOnly = !generate.IsEnabled;
            connect.IsEnabled = generate.IsEnabled && optIn.IsChecked == true && !string.IsNullOrWhiteSpace(recoveryCode.Text);
            syncNow.IsEnabled = local && bundle is not null && summary.Enabled;
            rotate.IsEnabled = syncNow.IsEnabled && summary.PendingDeletions == 0;
            stop.IsEnabled = local && summary.Enabled;
            scope.IsEnabled = ready;
            roles.IsEnabled = local && summary.Enabled;
            roles.Visibility = scope.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            remove.IsEnabled = local && (scope.SelectedIndex == 3 || summary.Enabled)
                && (scope.SelectedIndex != 0 || roles.SelectedItem is ComboBoxItem);
            retry.IsEnabled = ready && summary.PendingDeletions > 0;
            confirm.IsEnabled = open && !busy && confirmedAction is not null;
            cancel.IsEnabled = !busy;
            website.IsEnabled = !busy;
            optIn.Visibility = summary.Enabled ? Visibility.Collapsed : Visibility.Visible;
            setup.Visibility = summary.Enabled ? Visibility.Collapsed : Visibility.Visible;
        }

        async Task RefreshAsync()
        {
            var slot = publisherAccounts.HoyoLabAccounts.ActiveSlotId;
            var enabledConsent = HasPublisherConsent("hsr");
            var nextSummary = await publisherAccounts.GetHsrSyncSummaryAsync(token);
            var nextBundle = !contextInvalidated && enabledConsent
                ? await publisherAccounts.GetHsrGameBundleSnapshotAsync(token) : null;
            if (!open || token.IsCancellationRequested) return;
            if (slot != publisherAccounts.HoyoLabAccounts.ActiveSlotId || enabledConsent != HasPublisherConsent("hsr"))
            {
                summary = nextSummary;
                InvalidateContext();
                return;
            }
            if (!contextInvalidated)
            {
                displayedSlot = slot;
                displayedConsent = enabledConsent;
            }
            summary = contextInvalidated ? nextSummary with { Enabled = false, LastSyncedAt = null } : nextSummary;
            bundle = contextInvalidated ? null : nextBundle;
            status.Text = contextInvalidated ? "The account changed. Reopen this window before a new account action."
                : !summary.Available ? "Saved sync status is unavailable. Nothing will be uploaded."
                : summary.Enabled ? $"Manual sync enabled · Last saved: {summary.LastSyncedAt?.ToLocalTime().ToString("g") ?? "not yet"}"
                : "Manual sync is off on this PC.";
            if (summary.PendingDeletions > 0)
                status.Text += $"\n{summary.PendingDeletions} deletion request(s) still need confirmation. Use Retry deletion; Nyx also retries at startup.";
            if (displayedSlot is null || !displayedConsent)
                status.Text += "\nConnect and choose an account to start a new sync. Existing deletion requests can still be retried here.";
            var selected = (roles.SelectedItem as ComboBoxItem)?.Tag as PublisherRoleBinding;
            roles.Items.Clear();
            foreach (var role in bundle?.Roles ?? [])
            {
                var item = new ComboBoxItem
                {
                    Content = $"{role.Role.Nickname ?? "Star Rail"} · {role.Role.ReadableRegion} · {role.Role.Binding.RoleId}",
                    Tag = role.Role.Binding,
                };
                roles.Items.Add(item);
                if (role.Role.Binding == selected || (selected is null && role.Role.Binding == bundle?.SelectedRole))
                    roles.SelectedItem = item;
            }
            Render();
        }

        async Task RunAsync(Func<CancellationToken, Task<HoyoLabManualSyncResult>> action, bool accountAction = true)
        {
            if (busy || !open || publisherAccountActionInFlight) return;
            if (accountAction && (contextInvalidated || syncSlot != publisherAccounts.HoyoLabAccounts.ActiveSlotId
                    || !HasPublisherConsent("hsr")))
            {
                InvalidateContext();
                return;
            }
            busy = true;
            publisherAccountActionInFlight = true;
            ClearConfirmation();
            Render();
            RenderSelection();
            try
            {
                var result = await action(token);
                if (!open || token.IsCancellationRequested) return;
                if (result.RecoveryCode is not null && syncSlot == publisherAccounts.HoyoLabAccounts.ActiveSlotId
                    && HasPublisherConsent("hsr")) recoveryCode.Text = result.RecoveryCode;
                message.Text = HoyoSyncResultText(result.Status);
                await RefreshAsync();
                if (open && result.RecoveryCode is not null && recoveryCode.Text.Length > 0)
                    message.Text = summary.PendingDeletions > 0
                        ? "The new code is active. Keep it safe. Old-cloud deletion is still pending, so the old code may still work. Use Retry deletion; review My HoYo with the old code if a newer copy prevents removal."
                        : "The new code is active and the old cloud copy was removed. Keep the new code safe.";
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (open) message.Text = "The action could not finish. Check the saved status before trying again.";
            }
            finally
            {
                busy = false;
                publisherAccountActionInFlight = false;
                if (open) Render();
                RenderSelection();
            }
        }

        void Review(string text, Func<CancellationToken, Task<HoyoLabManualSyncResult>> action)
        {
            if (busy || !open) return;
            confirmedAction = action;
            confirmation.Text = text;
            confirmationPanel.Visibility = Visibility.Visible;
            Render();
            cancel.Focus(FocusState.Programmatic);
        }

        generate.Click += (_, _) => { recoveryCode.Text = HoyoLabSyncCoordinator.GenerateRecoveryCode(); };
        recoveryCode.TextChanged += (_, _) => Render();
        optIn.Checked += (_, _) => Render();
        optIn.Unchecked += (_, _) => Render();
        connect.Click += async (_, _) =>
        {
            var enteredCode = recoveryCode.Text.Trim();
            await RunAsync(ct => publisherAccounts.ConnectHsrSyncAsync(syncSlot!, enteredCode, ct));
        };
        syncNow.Click += async (_, _) => await RunAsync(ct => publisherAccounts.SyncHsrNowAsync(syncSlot!, ct));
        stop.Click += (_, _) => Review(
            "Stop syncing on this PC and forget its sync key? Local account data and the cloud copy stay. Keep your recovery code to reconnect.",
            async ct =>
            {
                var result = await publisherAccounts.StopHsrSyncAsync(syncSlot!, ct);
                if (result.Status == HoyoLabManualSyncStatus.Completed) recoveryCode.Text = string.Empty;
                return result;
            });
        rotate.Click += (_, _) => Review(
            "Create a new recovery code and retire the old copy if it has not changed? Keep the new code for your other devices. A newer saved copy stops old-code removal; pull history is unchanged.",
            ct => publisherAccounts.RotateHsrSyncCodeAsync(syncSlot!, ct));
        retry.Click += async (_, _) => await RunAsync(publisherAccounts.RetryHoyoLabSyncDeletionsAsync, accountAction: false);
        website.Click += async (_, _) => await OpenFixedDestinationAsync(new Uri("https://pengo.gg/nyx/my-hoyo"), "My HoYo");
        scope.SelectionChanged += (_, _) => { ClearConfirmation(); Render(); };
        roles.SelectionChanged += (_, _) => { ClearConfirmation(); Render(); };
        remove.Click += (_, _) =>
        {
            var slot = displayedSlot;
            if (slot is null) return;
            switch (scope.SelectedIndex)
            {
                case 0 when roles.SelectedItem is ComboBoxItem { Tag: PublisherRoleBinding binding } item:
                    Review($"Remove saved data for {item.Content}, here and in the cloud? Other roles and pull history stay.",
                        ct => publisherAccounts.DeleteHsrSyncedRoleAsync(syncSlot!, binding, ct));
                    break;
                case 1:
                    Review("Delete this account's Star Rail cloud copy and stop syncing it here? Local snapshots and pull history stay.",
                        ct => publisherAccounts.DeleteHsrCloudCopyAsync(syncSlot!, ct));
                    break;
                case 2:
                    Review("Remove this HoYoLAB account from this PC and delete all its HoYo cloud data? Pull history stays. Offline deletion remains pending until confirmed.",
                        ct => publisherAccounts.RemoveHoyoLabAccountEverywhereAsync(slot, ct));
                    break;
                case 3:
                    Review("Remove this saved HoYoLAB account from this PC only? Its HoYo cloud copy and pull history stay. Keep your recovery code before removing it.",
                        async ct => new(await publisherAccounts.ForgetHoyoLabAccountAsync(slot, ct)
                            ? HoyoLabManualSyncStatus.Completed : HoyoLabManualSyncStatus.LocalStorageUnavailable));
                    break;
            }
        };
        confirm.Click += async (_, _) =>
        {
            var action = confirmedAction;
            if (action is not null) await RunAsync(action);
        };
        cancel.Click += (_, _) => { ClearConfirmation(); Render(); };

        void OnAccountsUpdated(object? sender, EventArgs e)
        {
            // Capture the change before queueing UI work; A -> B -> A must not
            // restore an old confirmation or secret while the queue is busy.
            var accountChanged = syncSlot != publisherAccounts.HoyoLabAccounts.ActiveSlotId
                || consentWhenOpened != HasPublisherConsent("hsr");
            if (!accountChanged) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!open || contextInvalidated) return;
                InvalidateContext();
            });
        }
        publisherAccounts.Updated += OnAccountsUpdated;
        dialog.Closed += (_, _) =>
        {
            open = false;
            cancellation.Cancel();
            recoveryCode.Text = string.Empty;
            roles.Items.Clear();
            bundle = null;
            ClearConfirmation();
        };
        using var closeOnCancel = token.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
        try
        {
            await RefreshAsync();
            token.ThrowIfCancellationRequested();
            await dialog.ShowAsync().AsTask(token);
        }
        finally
        {
            open = false;
            cancellation.Cancel();
            recoveryCode.Text = string.Empty;
            roles.Items.Clear();
            bundle = null;
            ClearConfirmation();
            publisherAccounts.Updated -= OnAccountsUpdated;
        }
    }

    private static string HoyoSyncResultText(HoyoLabManualSyncStatus status) => status switch
    {
        HoyoLabManualSyncStatus.Completed => "Finished. Check the saved status above.",
        HoyoLabManualSyncStatus.NotEnabled => "Choose a connected HoYoLAB account and enable manual sync first.",
        HoyoLabManualSyncStatus.NoLocalData => "No remembered Star Rail data is available. Choose a region and enable the data you want to remember in Accounts.",
        HoyoLabManualSyncStatus.InvalidRecoveryCode => "That recovery code is not valid. Check it and try again.",
        HoyoLabManualSyncStatus.Conflict => "The saved copies disagree, or newer data appeared during deletion. Nothing was forced over it. Review the other device or My HoYo before retrying.",
        HoyoLabManualSyncStatus.DeletionPending => "A deletion is still pending. Use Retry deletion before reconnecting this copy.",
        HoyoLabManualSyncStatus.LocalStorageUnavailable => "Protected local data could not be read or saved. The action is not confirmed complete.",
        HoyoLabManualSyncStatus.InvalidCloudData => "The cloud copy could not be safely opened. Check your recovery code; existing data was not replaced.",
        HoyoLabManualSyncStatus.AuthenticationFailed => "This code no longer has access. Check whether it was changed or deleted on another device.",
        HoyoLabManualSyncStatus.NetworkUnavailable or HoyoLabManualSyncStatus.TimedOut => "Pengo could not be reached. Any saved deletion request remains pending; retry when online.",
        HoyoLabManualSyncStatus.RateLimited => "Pengo asked Nyx to wait. Try again later.",
        HoyoLabManualSyncStatus.TooLarge => "This copy is too large to sync. Nothing was silently cut out.",
        _ => "Canceled. Check the saved status before retrying.",
    };
}
