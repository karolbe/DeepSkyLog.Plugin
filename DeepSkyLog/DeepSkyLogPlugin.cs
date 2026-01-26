using DeepSkyLog.NINAPlugin.Properties;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
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
        private readonly AuthenticationService _authService;
        private bool _isAuthenticating;
        private string _authStatusMessage;
        private string _authenticatedUsername;

        [ImportingConstructor]
        public DeepSkyLogPlugin(IProfileService profileService, IImageSaveMediator imageSaveMediator, IImageHistoryVM imageHistory) {

            if (Settings.Default.UpdateSettings) {
                Settings.Default.Upgrade();
                Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Settings.Default);
            }
            new DeepSkyLogWatcher(imageSaveMediator);

            // Initialize authentication service
            _authService = new AuthenticationService();
            _authService.OnTokenReceived += OnTokenReceived;
            _authService.OnAuthenticationFailed += OnAuthenticationFailed;

            // Initialize commands
            LoginCommand = new RelayCommand(ExecuteLogin, CanExecuteLogin);
            LogoutCommand = new RelayCommand(ExecuteLogout, CanExecuteLogout);
            OpenWebAppCommand = new RelayCommand(ExecuteOpenWebApp);

            // Initialize collections with empty placeholder items
            _locations.Add(new DeepSkyLogWatcher.Location { Id = 0, Name = "Select Location..." });
            _equipment.Add(new DeepSkyLogWatcher.Equipment { Id = 0, Name = "Select Equipment..." });

            // Validate existing token and load data if authenticated
            if (IsAuthenticated) {
                Task.Run(ValidateAndLoadDataAsync);
            }
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
            get => Settings.Default.DeepSkyLogKey;
            set {
                Settings.Default.DeepSkyLogKey = value;
                Settings.Default.Save();
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

        public ObservableCollection<DeepSkyLogWatcher.Location> Locations => _locations;
        public ObservableCollection<DeepSkyLogWatcher.Equipment> Equipment => _equipment;

        public DeepSkyLogWatcher.Location SelectedLocation {
            get => _selectedLocation ?? _locations.FirstOrDefault(l => l.Id == Settings.Default.SelectedLocationId);
            set {
                _selectedLocation = value;
                if (value != null) {
                    Settings.Default.SelectedLocationId = value.Id;
                    Settings.Default.Save();
                }
                RaisePropertyChanged();
            }
        }

        public DeepSkyLogWatcher.Equipment SelectedEquipment {
            get => _selectedEquipment ?? _equipment.FirstOrDefault(e => e.Id == Settings.Default.SelectedEquipmentId);
            set {
                _selectedEquipment = value;
                if (value != null) {
                    Settings.Default.SelectedEquipmentId = value.Id;
                    Settings.Default.Save();
                }
                RaisePropertyChanged();
            }
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