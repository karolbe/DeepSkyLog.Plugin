using DeepSkyLog.NINAPlugin.Properties;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System;
using System.Linq;
using System.Windows;

namespace DeepSkyLog.NINAPlugin {

    [Export(typeof(IPluginManifest))]
    public class DeepSkyLogPlugin : PluginBase, INotifyPropertyChanged {
        private ObservableCollection<DeepSkyLogWatcher.Location> _locations = new ObservableCollection<DeepSkyLogWatcher.Location>();
        private ObservableCollection<DeepSkyLogWatcher.Equipment> _equipment = new ObservableCollection<DeepSkyLogWatcher.Equipment>();
        private DeepSkyLogWatcher.Location _selectedLocation;
        private DeepSkyLogWatcher.Equipment _selectedEquipment;
        private string _parkedUploadsLabel;
        private readonly AuthenticationService _authService;
        private readonly TelemetryCollector _telemetryCollector;
        private readonly TelemetryUploader _telemetryUploader;
        private bool _isAuthenticating;
        private string _authStatusMessage;
        private string _authenticatedUsername;
        private string _selectionWarning;
        private string _updateNotice;

        [ImportingConstructor]
        public DeepSkyLogPlugin(IProfileService profileService,
                                IImageSaveMediator imageSaveMediator,
                                IImageHistoryVM imageHistory,
                                ITelescopeMediator telescopeMediator,
                                ISafetyMonitorMediator safetyMonitorMediator,
                                IDomeMediator domeMediator,
                                IFocuserMediator focuserMediator,
                                IGuiderMediator guiderMediator,
                                ICameraMediator cameraMediator,
                                ISequenceMediator sequenceMediator) {

            if (Settings.Default.UpdateSettings) {
                Settings.Default.Upgrade();
                Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Settings.Default);
            }
            new DeepSkyLogWatcher(imageSaveMediator);
            DeepSkyLogWatcher.UploadRejected += OnUploadRejected;
            DeepSkyLogWatcher.UploadSucceeded += OnUploadSucceeded;

            // Live session telemetry. Wrapped because a failure to attach to a mediator must not
            // take down the plugin — frame uploads are the primary job and have to keep working.
            try {
                _telemetryCollector = new TelemetryCollector(telescopeMediator, safetyMonitorMediator,
                    domeMediator, focuserMediator, guiderMediator, cameraMediator, sequenceMediator,
                    imageSaveMediator, imageHistory);
                _telemetryUploader = new TelemetryUploader(_telemetryCollector);
                _telemetryUploader.Start();
            } catch (Exception ex) {
                // Log the whole exception: a bare Message here hid a NullReferenceException with no
                // indication of where it came from.
                Logger.Error("DeepSkyLog live telemetry unavailable", ex);
            }

            // Initialize authentication service
            _authService = new AuthenticationService();
            _authService.OnTokenReceived += OnTokenReceived;
            _authService.OnAuthenticationFailed += OnAuthenticationFailed;

            // Initialize commands
            LoginCommand = new RelayCommand(ExecuteLogin, CanExecuteLogin);
            LogoutCommand = new RelayCommand(ExecuteLogout, CanExecuteLogout);
            OpenWebAppCommand = new RelayCommand(ExecuteOpenWebApp);
            RefreshCommand = new RelayCommand(ExecuteRefresh, CanExecuteRefresh);
            RetryParkedUploadsCommand = new RelayCommand(ExecuteRetryParkedUploads);
            RefreshParkedUploads();

            // Initialize collections with empty placeholder items
            _locations.Add(new DeepSkyLogWatcher.Location { Id = 0, Name = "Select Location..." });
            _equipment.Add(new DeepSkyLogWatcher.Equipment { Id = 0, Name = "Select Equipment..." });

            // Validate existing token and load data if authenticated
            if (IsAuthenticated) {
                Task.Run(ValidateAndLoadDataAsync);
            }

            // Independent of sign-in: an outdated plugin is worth reporting even to a user who has
            // not connected an account yet, and the manifest endpoint needs no token.
            Task.Run(CheckForUpdateAsync);
        }

        private async Task ValidateAndLoadDataAsync() {
            var validationResult = await _authService.ValidateTokenAsync(DeepSkyLogKey);

            Application.Current?.Dispatcher?.Invoke(() => {
                if (validationResult.IsValid) {
                    _authenticatedUsername = validationResult.Username;
                    AuthStatusMessage = string.Empty; // Clear status - username shown in UI
                    RaisePropertyChanged(nameof(AuthenticatedUsername));
                } else if (validationResult.NeedsReauthentication) {
                    // Token expired or invalid - clear it and notify user
                    Logger.Warning($"DeepSkyLog: Token validation failed: {validationResult.Error}");
                    DeepSkyLogKey = string.Empty;
                    AuthStatusMessage = "Session expired. Please login again.";
                    RaisePropertyChanged(nameof(IsAuthenticated));
                    RaisePropertyChanged(nameof(CanLogin));
                    return;
                } else {
                    // Network error or other issue - keep token but show warning
                    AuthStatusMessage = $"Could not validate connection: {validationResult.Error}";
                }
            });

            // Load locations and equipment
            await LoadDataAsync();
        }

        public ICommand LoginCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand OpenWebAppCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand RetryParkedUploadsCommand { get; }

        public bool IsAuthenticated => !string.IsNullOrEmpty(DeepSkyLogKey);

        public bool IsAuthenticating {
            get => _isAuthenticating;
            set {
                _isAuthenticating = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CanLogin));
            }
        }

        public bool CanLogin => !IsAuthenticating && !IsAuthenticated;

        public string AuthStatusMessage {
            get => _authStatusMessage;
            set {
                _authStatusMessage = value;
                RaisePropertyChanged();
            }
        }

        public string AuthenticatedUsername {
            get => _authenticatedUsername;
            set {
                _authenticatedUsername = value;
                RaisePropertyChanged();
            }
        }

        private void ExecuteLogin(object parameter) {
            if (IsAuthenticating) return;

            IsAuthenticating = true;
            AuthStatusMessage = "Opening browser for authentication...";

            Task.Run(async () => {
                try {
                    await _authService.StartAuthenticationAsync();
                } finally {
                    Application.Current?.Dispatcher?.Invoke(() => {
                        IsAuthenticating = false;
                    });
                }
            });
        }

        private bool CanExecuteLogin(object parameter) => CanLogin;

        private void ExecuteLogout(object parameter) {
            DeepSkyLogKey = string.Empty;
            AuthStatusMessage = "Logged out successfully.";

            // Clear collections
            Application.Current?.Dispatcher?.Invoke(() => {
                _locations.Clear();
                _locations.Add(new DeepSkyLogWatcher.Location { Id = 0, Name = "Select Location..." });
                _equipment.Clear();
                _equipment.Add(new DeepSkyLogWatcher.Equipment { Id = 0, Name = "Select Equipment..." });

                RaisePropertyChanged(nameof(IsAuthenticated));
                RaisePropertyChanged(nameof(CanLogin));
                RaisePropertyChanged(nameof(Locations));
                RaisePropertyChanged(nameof(Equipment));
            });
        }

        private bool CanExecuteLogout(object parameter) => IsAuthenticated;

        private void ExecuteRefresh(object parameter) {
            Task.Run(LoadDataAsync);
        }

        private bool CanExecuteRefresh(object parameter) => IsAuthenticated;

        private void ExecuteOpenWebApp(object parameter) {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = "https://app.deepskylog.space",
                UseShellExecute = true
            });
        }

        private void OnTokenReceived(string token) {
            Application.Current?.Dispatcher?.Invoke(() => {
                DeepSkyLogKey = token;
                AuthStatusMessage = string.Empty; // Will show username in UI after validation
                IsAuthenticating = false;
                RaisePropertyChanged(nameof(IsAuthenticated));
                RaisePropertyChanged(nameof(CanLogin));
            });

            // Validate token and load data after successful authentication
            Task.Run(ValidateAndLoadDataAsync);
        }

        private void OnAuthenticationFailed(string error) {
            Application.Current?.Dispatcher?.Invoke(() => {
                AuthStatusMessage = $"Authentication failed: {error}";
                IsAuthenticating = false;
            });
        }

        public bool DeepSkyLogEnabled {
            get => Settings.Default.DeepSkyLogEnabled;
            set {
                Settings.Default.DeepSkyLogEnabled = value;
                Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        public string DeepSkyLogKey {
            get => TokenStorage.Load();
            set {
                TokenStorage.Save(value);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsAuthenticated));
                RaisePropertyChanged(nameof(CanLogin));
                // Refresh data when key changes
                if (!string.IsNullOrEmpty(value)) {
                    Task.Run(LoadDataAsync);
                }
            }
        }

        public bool DeepSkyLogAllowSnapshots {
            get => Settings.Default.DeepSkyLogAllowSnapshots;
            set {
                Settings.Default.DeepSkyLogAllowSnapshots = value;
                Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Clamped to the same 5–300s range the uploader enforces, so a typo in the options box
        /// cannot turn the plugin into a request flood or silence it for an hour.
        /// </summary>
        public int DeepSkyLogTelemetryIntervalSeconds {
            get => Settings.Default.DeepSkyLogTelemetryIntervalSeconds;
            set {
                Settings.Default.DeepSkyLogTelemetryIntervalSeconds = Math.Min(Math.Max(value, 5), 300);
                Settings.Default.Save();
                RaisePropertyChanged();
            }
        }

        public override Task Teardown() {
            DeepSkyLogWatcher.UploadRejected -= OnUploadRejected;
            DeepSkyLogWatcher.UploadSucceeded -= OnUploadSucceeded;
            _telemetryUploader?.Dispose();
            _telemetryCollector?.Dispose();
            return base.Teardown();
        }

        public ObservableCollection<DeepSkyLogWatcher.Location> Locations => _locations;
        public ObservableCollection<DeepSkyLogWatcher.Equipment> Equipment => _equipment;

        /// <summary>
        /// Set when a saved location/equipment ID no longer resolves against the account, so the
        /// options page can say so instead of just showing an empty dropdown.
        /// </summary>
        public string SelectionWarning {
            get => _selectionWarning;
            private set {
                _selectionWarning = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Set when DeepSkyLog has published a newer build of this plugin, so the options page can
        /// say so. NINA installs plugins, not us, so this is a notice and nothing more.
        /// </summary>
        public string UpdateNotice {
            get => _updateNotice;
            private set {
                _updateNotice = value;
                RaisePropertyChanged();
            }
        }

        public DeepSkyLogWatcher.Location SelectedLocation {
            get => _selectedLocation ?? _locations.FirstOrDefault(l => l.Id == Settings.Default.SelectedLocationId);
            set {
                _selectedLocation = value;
                // Only a genuine change counts: WPF re-coerces the selection when the list
                // refreshes, and that must not clear a warning the user has not acted on.
                if (value != null && value.Id != Settings.Default.SelectedLocationId) {
                    Settings.Default.SelectedLocationId = value.Id;
                    Settings.Default.Save();
                    OnSelectionChanged();
                }
                RaisePropertyChanged();
            }
        }

        public DeepSkyLogWatcher.Equipment SelectedEquipment {
            get => _selectedEquipment ?? _equipment.FirstOrDefault(e => e.Id == Settings.Default.SelectedEquipmentId);
            set {
                _selectedEquipment = value;
                if (value != null && value.Id != Settings.Default.SelectedEquipmentId) {
                    Settings.Default.SelectedEquipmentId = value.Id;
                    Settings.Default.Save();
                    OnSelectionChanged();
                }
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Changing the selection clears the warning but deliberately does NOT replay the parked
        /// uploads — replaying on a dropdown change meant one mis-click shipped the backlog to the
        /// wrong equipment before the user could correct it. Replay is the button below, an
        /// explicit act taken after the user has seen what is selected.
        /// </summary>
        private void OnSelectionChanged() {
            SelectionWarning = null;
            RefreshParkedUploads();
        }

        /// <summary>Null when nothing is parked, which hides the retry button entirely.</summary>
        public string ParkedUploadsLabel {
            get => _parkedUploadsLabel;
            private set {
                _parkedUploadsLabel = value;
                RaisePropertyChanged();
            }
        }

        private void RefreshParkedUploads() {
            int count = DeepSkyLogWatcher.CountParkedUploads();
            string label = count > 0
                ? $"Retry {count} parked upload{(count == 1 ? "" : "s")} with the selection above"
                : null;
            Application.Current?.Dispatcher?.Invoke(() => ParkedUploadsLabel = label);
        }

        private void ExecuteRetryParkedUploads(object parameter) {
            Task.Run(async () => {
                try {
                    await DeepSkyLogWatcher.RetryParkedUploadsAsync();
                } catch (Exception ex) {
                    Logger.Error("DeepSkyLog retry of parked uploads failed", ex);
                } finally {
                    RefreshParkedUploads();
                }
            });
        }

        /// <summary>
        /// A live rejection, surfaced two ways: red text on the options page (persists until fixed)
        /// and a NINA notification bubble (catches the user who is not looking at the options).
        /// The bubble fires only when the warning transitions from clear to set — a night of
        /// rejected frames is one toast, not hundreds — and re-arms when the user changes their
        /// selection, so a fix that did not take produces a fresh one.
        /// </summary>
        private void OnUploadRejected(string serverMessage) {
            bool firstSinceClear = string.IsNullOrEmpty(_selectionWarning);

            string warning = $"DeepSkyLog rejected the last upload: {serverMessage} " +
                             "Frames are kept locally — check your location and equipment below, " +
                             "then use the retry button to send them.";
            Application.Current?.Dispatcher?.Invoke(() => SelectionWarning = warning);
            RefreshParkedUploads();

            if (firstSinceClear) {
                NINA.Core.Utility.Notification.Notification.ShowError(
                    $"DeepSkyLog rejected an upload: {serverMessage} " +
                    "Frames are kept locally — check your location and equipment selection in the plugin options.");
            }
        }

        /// <summary>
        /// Reports a newer published build, once, on startup.
        /// </summary>
        /// <remarks>
        /// A warning rather than an error: unless the build is below the server's floor nothing is
        /// broken yet, and the toast exists to catch the user who never opens the plugin options.
        /// </remarks>
        private async Task CheckForUpdateAsync() {
            string notice = await UpdateCheckService.CheckAsync();
            if (string.IsNullOrEmpty(notice)) return;

            Logger.Info($"DeepSkyLog: {notice}");
            Application.Current?.Dispatcher?.Invoke(() => UpdateNotice = notice);
            NINA.Core.Utility.Notification.Notification.ShowWarning(notice);
        }

        /// <summary>A delivered upload proves the selection works, so any stale warning comes down.</summary>
        private void OnUploadSucceeded() {
            if (string.IsNullOrEmpty(_selectionWarning)) return;
            Application.Current?.Dispatcher?.Invoke(() => SelectionWarning = null);
        }

        private async Task LoadDataAsync() {
            if (string.IsNullOrEmpty(DeepSkyLogKey)) return;

            try {
                var locations = await DeepSkyLogWatcher.GetLocationsAsync(DeepSkyLogKey);
                var equipments = await DeepSkyLogWatcher.GetEquipmentsAsync(DeepSkyLogKey);

                // Update collections on UI thread
                Application.Current?.Dispatcher?.Invoke(() => {
                    _locations.Clear();
                    _locations.Add(new DeepSkyLogWatcher.Location { Id = 0, Name = "Select Location..." });
                    foreach (var location in locations) {
                        _locations.Add(location);
                    }

                    _equipment.Clear();
                    _equipment.Add(new DeepSkyLogWatcher.Equipment { Id = 0, Name = "Select Equipment..." });
                    foreach (var equipment in equipments) {
                        _equipment.Add(equipment);
                    }

                    RaisePropertyChanged(nameof(Locations));
                    RaisePropertyChanged(nameof(Equipment));
                    RaisePropertyChanged(nameof(SelectedLocation));
                    RaisePropertyChanged(nameof(SelectedEquipment));

                    // A saved ID that is no longer in the account leaves the dropdown blank but
                    // keeps being sent on every upload — say so rather than letting it look unset.
                    SelectionWarning = DeepSkyLogWatcher.ValidateSelectedIds(locations, equipments);
                });
            } catch (Exception ex) {
                Logger.Debug($"Failed to load locations/equipment: {ex.Message}");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RelayCommand : ICommand {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null) {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object parameter) => _execute(parameter);
    }
}