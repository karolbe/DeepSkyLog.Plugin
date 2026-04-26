using DeepSkyLog.NINAPlugin.Properties;
using Namotion.Reflection;
using Newtonsoft.Json;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using static NINA.Equipment.Model.CaptureSequence;

namespace DeepSkyLog.NINAPlugin {

    public class DeepSkyLogWatcher {
        private static readonly HttpClient client = new ();
        private static readonly string TempFolderPath = Path.GetTempPath();
        private static readonly ConcurrentQueue<string> retryQueue = new ();
        private static readonly SemaphoreSlim retrySemaphore = new(1, 1);

        public DeepSkyLogWatcher(IImageSaveMediator imageSaveMediator) {
            imageSaveMediator.ImageSaved += ImageSaveMeditator_ImageSaved;
            Logger.Info("DeepSkyLog is loading");
        }
        private string GetImageFilePath(Uri imageUri) {
            return HttpUtility.UrlDecode(imageUri.AbsolutePath);
        }

        private void ImageSaveMeditator_ImageSaved(object sender, ImageSavedEventArgs msg) {
            if (!Settings.Default.DeepSkyLogEnabled) {
                Logger.Debug("DeepSkyLog not enabled");
                return;
            }
            Logger.Info("DeepSkyLog is enabled");

//            if (msg.MetaData.Image.ImageType != ImageTypes.LIGHT && Settings.Default.DeepSkyLogAllowSnapshots == false) {
//                Logger.Debug("Image is not a light, skipping...");
//                return;
//            }

            try {
                Task.Run(() => ProcessImageSave(msg));
            } catch (Exception e) {
                Logger.Warning($"session metadata save failed: {e.Message}");
            }
        }
        private async Task ProcessImageSave(ImageSavedEventArgs msg) {
            try {
                // Attempt to retry any failed requests first
                await RetryFailedRequestsAsync();

                WeatherMetaDataRecord weatherRecord = new WeatherMetaDataRecord(msg);
                ImageMetaDataRecord imageMetaDataRecord = new ImageMetaDataRecord(msg, GetImageFilePath(msg.PathToImage));
                AcquisitionMetaDataRecord acquisitionMetaDataRecord = new AcquisitionMetaDataRecord(msg);

                // Calculate checksum of the first 50KB of the image file
                string imageFilePath = GetImageFilePath(msg.PathToImage);
                string checksum = CalculateFileChecksum(imageFilePath);

                var combinedData = new {
                    weatherRecord,
                    imageMetaDataRecord,
                    acquisitionMetaDataRecord,
                    Checksum = checksum
                };

                // Serialize to JSON
                string json = JsonConvert.SerializeObject(combinedData);
                string tempFilePath = GetTempFilePath(msg.MetaData.Image.ExposureStart);
                Logger.Debug($"Saved DSL file to {tempFilePath}");

                // Get selected location and equipment IDs
                var (locationId, equipmentId) = GetSelectedIds();
                Logger.Debug($"Using location ID: {locationId}, equipment ID: {equipmentId}");

                // Try posting the data with location and equipment parameters
                if (!await TryPostToServerAsync(json, locationId, equipmentId)) {
                    SaveFailedRequest(tempFilePath, json);
                }
            } catch (Exception ex) {
                Logger.Debug($"Unexpected error in ProcessImageSave: {ex.Message}");
            }
        }

        private static async Task<bool> TryPostToServerAsync(string json, string locationId = null, string equipmentId = null) {
            try {
                Logger.Debug($"Preparing server request: {json}");

                // Build query parameters
                var queryParams = new List<string>();
                if (!string.IsNullOrEmpty(locationId))
                    queryParams.Add($"location={Uri.EscapeDataString(locationId)}");
                if (!string.IsNullOrEmpty(equipmentId))
                    queryParams.Add($"equipment={Uri.EscapeDataString(equipmentId)}");
                Logger.Debug($"Request Query Params: location={locationId} equipment={equipmentId}");
                var queryString = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "";
                var baseUrl = "https://app.deepskylog.space/api/v1/nina/upload";
                var fullUrl = $"{baseUrl}{queryString}";

                using var request = new HttpRequestMessage(HttpMethod.Post, fullUrl) {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenStorage.Load());

                using var response = await client.SendAsync(request);
                Logger.Debug($"Server response: {response.StatusCode}");

                if (response.IsSuccessStatusCode) {
                    return true;
                } else {
                    string responseContent = await response.Content.ReadAsStringAsync();
                    Logger.Debug($"Server responded with: {response.StatusCode} - {responseContent}");
                }
            } catch (Exception ex) {
                Logger.Debug($"Error posting data: {ex.Message}");
            }
            return false;
        }

        private static void SaveFailedRequest(string filePath, string json) {
            try {
                File.WriteAllText(filePath, json);
                retryQueue.Enqueue(filePath);
                Logger.Debug($"Failed request saved to {filePath} for retry.");
            } catch (Exception ex) {
                Logger.Debug($"Error saving failed request: {ex.Message}");
            }
        }

        private static async Task RetryFailedRequestsAsync() {
            if (!retrySemaphore.Wait(0)) {
                return; // Avoid concurrent retries
            }

            try {
                foreach (string filePath in Directory.GetFiles(TempFolderPath, "dsl_request_*.json")) {
                    retryQueue.Enqueue(filePath);
                }

                // Get current selected location and equipment IDs for retries
                var (locationId, equipmentId) = GetSelectedIds();

                while (retryQueue.TryDequeue(out string filePath)) {
                    string json = File.ReadAllText(filePath);
                    if (await TryPostToServerAsync(json, locationId, equipmentId)) {
                        File.Delete(filePath);
                        Logger.Debug($"Retried request successfully sent and removed {filePath}.");
                    }
                }
            } catch (Exception ex) {
                Logger.Debug($"Error during retry process: {ex.Message}");
            } finally {
                retrySemaphore.Release();
            }
        }

        private static string GetTempFilePath(DateTime exposureStart) {
            string fileName = $"dsl_request_{exposureStart:yyyyMMdd_HHmmss}.json";
            return Path.Combine(TempFolderPath, fileName);
        }

        public static async Task<List<Location>> GetLocationsAsync(string apiKey) {
            try {
                string baseUrl = "https://app.deepskylog.space";
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/list/locations");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                using var response = await client.SendAsync(request);
                string responseContent = await response.Content.ReadAsStringAsync();

                Logger.Debug($"Locations API response: {response.StatusCode}");

                if (response.IsSuccessStatusCode) {
                    var locationResponse = JsonConvert.DeserializeObject<LocationListResponse>(responseContent);
                    if (locationResponse?.Success == true) {
                        return locationResponse.Locations ?? new List<Location>();
                    }
                } else {
                    var errorResponse = JsonConvert.DeserializeObject<ApiErrorResponse>(responseContent);
                    Logger.Warning($"Failed to fetch locations: {errorResponse?.Message ?? response.StatusCode.ToString()}");
                }
            } catch (Exception ex) {
                Logger.Warning($"Error fetching locations: {ex.Message}");
            }
            return new List<Location>();
        }

        public static async Task<List<Equipment>> GetEquipmentsAsync(string apiKey) {
            try {
                string baseUrl = "https://app.deepskylog.space";
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/list/equipments");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                using var response = await client.SendAsync(request);
                string responseContent = await response.Content.ReadAsStringAsync();

                Logger.Debug($"Equipment API response: {response.StatusCode}");

                if (response.IsSuccessStatusCode) {
                    var equipmentResponse = JsonConvert.DeserializeObject<EquipmentListResponse>(responseContent);
                    if (equipmentResponse?.Success == true) {
                        return equipmentResponse.Equipments ?? new List<Equipment>();
                    }
                } else {
                    var errorResponse = JsonConvert.DeserializeObject<ApiErrorResponse>(responseContent);
                    Logger.Warning($"Failed to fetch equipment: {errorResponse?.Message ?? response.StatusCode.ToString()}");
                }
            } catch (Exception ex) {
                Logger.Warning($"Error fetching equipment: {ex.Message}");
            }
            return new List<Equipment>();
        }

        private static (string locationId, string equipmentId) GetSelectedIds() {
            string locationId = null;
            string equipmentId = null;

            if (Settings.Default.SelectedLocationId > 0) {
                locationId = Settings.Default.SelectedLocationId.ToString();
                Logger.Debug($"Using location ID: {locationId}");
            }

            if (Settings.Default.SelectedEquipmentId > 0) {
                equipmentId = Settings.Default.SelectedEquipmentId.ToString();
                Logger.Debug($"Using equipment ID: {equipmentId}");
            }

            Logger.Debug($"GetSelectedIds returning: location='{locationId}', equipment='{equipmentId}'");
            return (locationId, equipmentId);
        }

        private static string CalculateFileChecksum(string filePath) {
            try {
                if (!File.Exists(filePath)) {
                    Logger.Warning($"File not found for checksum calculation: {filePath}");
                    return null;
                }

                const int bufferSize = 50 * 1024; // 50KB
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha256 = SHA256.Create()) {
                    byte[] buffer = new byte[bufferSize];
                    int bytesRead = fileStream.Read(buffer, 0, bufferSize);
                    
                    if (bytesRead == 0) {
                        Logger.Warning($"File is empty for checksum calculation: {filePath}");
                        return null;
                    }

                    // If we read less than 50KB, resize the buffer to actual bytes read
                    if (bytesRead < bufferSize) {
                        Array.Resize(ref buffer, bytesRead);
                    }

                    byte[] hashBytes = sha256.ComputeHash(buffer);
                    string checksum = Convert.ToHexString(hashBytes).ToLowerInvariant();
                    
                    Logger.Debug($"Calculated checksum for {filePath} (first {bytesRead} bytes): {checksum}");
                    return checksum;
                }
            } catch (Exception ex) {
                Logger.Warning($"Error calculating checksum for {filePath}: {ex.Message}");
                return null;
            }
        }

        public class ImageMetaDataRecord {
            public int ExposureNumber { get; set; }
            public string FilePath { get; set; }
            public string FilterName { get; set; }
            public string ExposureStart { get; set; }
            public double Duration { get; set; }
            public string Binning { get; set; }
            public double CameraTemp { get; set; }
            public double CameraTargetTemp { get; set; }
            public int Gain { get; set; }
            public int Offset { get; set; }
            public double ADUStDev { get; set; }
            public double ADUMean { get; set; }
            public double ADUMedian { get; set; }
            public int ADUMin { get; set; }
            public int ADUMax { get; set; }
            public int DetectedStars { get; set; }
            public double HFR { get; set; }
            public double HFRStDev { get; set; }
            public double FWHM { get; set; }
            public double Eccentricity { get; set; }
            public double GuidingRMS { get; set; }
            public double GuidingRMSArcSec { get; set; }
            public double GuidingRMSRA { get; set; }
            public double GuidingRMSRAArcSec { get; set; }
            public double GuidingRMSDEC { get; set; }
            public double GuidingRMSDECArcSec { get; set; }
            public int? FocuserPosition { get; set; }
            public double FocuserTemp { get; set; }
            public double RotatorPosition { get; set; }
            public string PierSide { get; set; }
            public double Airmass { get; set; }
            public string ExposureStartUTC { get; set; }
            public double MountRA { get; set; }
            public double MountDec { get; set; }

            public ImageMetaDataRecord() {
            }

            public ImageMetaDataRecord(ImageSavedEventArgs msg, string ImageFilePath) {
                ExposureNumber = msg.MetaData.Image.ExposureNumber;
                FilePath = ImageFilePath;
                FilterName = msg.Filter;
                ExposureStart = Utility.Utility.FormatDateTime(msg.MetaData.Image.ExposureStart);
                ExposureStartUTC = Utility.Utility.FormatDateTimeISO8601(msg.MetaData.Image.ExposureStart);
                Duration = Utility.Utility.ReformatDouble(msg.Duration);
                Binning = msg.MetaData.Image.Binning?.ToString();

                CameraTemp = Utility.Utility.ReformatDouble(msg.MetaData.Camera.Temperature);
                CameraTargetTemp = Utility.Utility.ReformatDouble(msg.MetaData.Camera.SetPoint);

                Gain = msg.MetaData.Camera.Gain;
                Offset = msg.MetaData.Camera.Offset;

                ADUStDev = Utility.Utility.ReformatDouble(msg.Statistics.StDev);
                ADUMean = Utility.Utility.ReformatDouble(msg.Statistics.Mean);
                ADUMedian = Utility.Utility.ReformatDouble(msg.Statistics.Median);
                ADUMin = msg.Statistics.Min;
                ADUMax = msg.Statistics.Max;

                DetectedStars = msg.StarDetectionAnalysis.DetectedStars;
                HFR = Utility.Utility.ReformatDouble(msg.StarDetectionAnalysis.HFR);
                HFRStDev = Utility.Utility.ReformatDouble(msg.StarDetectionAnalysis.HFRStDev);

                FWHM = GetHocusFocusMetric(msg.StarDetectionAnalysis, "FWHM");
                Eccentricity = GetHocusFocusMetric(msg.StarDetectionAnalysis, "Eccentricity");

                GuidingRMS = GetGuidingMetric(msg.MetaData.Image, msg.MetaData.Image?.RecordedRMS?.Total);
                GuidingRMSArcSec = GetGuidingMetricArcSec(msg.MetaData.Image, msg.MetaData.Image?.RecordedRMS?.Total);
                GuidingRMSRA = GetGuidingMetric(msg.MetaData.Image, msg.MetaData.Image?.RecordedRMS?.RA);
                GuidingRMSRAArcSec = GetGuidingMetricArcSec(msg.MetaData.Image, msg.MetaData.Image?.RecordedRMS?.RA);
                GuidingRMSDEC = GetGuidingMetric(msg.MetaData.Image, msg.MetaData.Image?.RecordedRMS?.Dec);
                GuidingRMSDECArcSec = GetGuidingMetricArcSec(msg.MetaData.Image, msg.MetaData.Image?.RecordedRMS?.Dec);

                FocuserPosition = msg.MetaData.Focuser.Position;
                FocuserTemp = Utility.Utility.ReformatDouble(msg.MetaData.Focuser.Temperature);
                RotatorPosition = Utility.Utility.ReformatDouble(msg.MetaData.Rotator.Position);
                PierSide = GetPierSide(msg.MetaData.Telescope.SideOfPier);

                Airmass = Utility.Utility.ReformatDouble(msg.MetaData.Telescope.Airmass);

                MountRA = msg.MetaData.Telescope.Coordinates.RADegrees;
                MountDec = msg.MetaData.Telescope.Coordinates.Dec;
            }

            private double GetHocusFocusMetric(IStarDetectionAnalysis starDetectionAnalysis, string propertyName) {
                return starDetectionAnalysis.HasProperty(propertyName) ?
                    (Double)starDetectionAnalysis.GetType().GetProperty(propertyName).GetValue(starDetectionAnalysis) :
                    Double.NaN;
            }

            private double GetGuidingMetric(ImageParameter image, double? metric) {
                return (image.RecordedRMS != null && metric != null) ? Utility.Utility.ReformatDouble((double)metric) : 0.0;
            }

            private double GetGuidingMetricArcSec(ImageParameter image, double? metric) {
                return (image.RecordedRMS != null && metric != null) ? Utility.Utility.ReformatDouble((double)(metric * image.RecordedRMS.Scale)) : 0.0;
            }

            private string GetPierSide(PierSide sideOfPier) {
                switch (sideOfPier) {
                    case NINA.Core.Enum.PierSide.pierEast: return "East";
                    case NINA.Core.Enum.PierSide.pierWest: return "West";
                    default: return "n/a";
                }
            }
        }
        public class WeatherMetaDataRecord {
            public int ExposureNumber { get; set; }
            public string ExposureStart { get; set; }
            public double Temperature { get; set; }
            public double DewPoint { get; set; }
            public double Humidity { get; set; }
            public double Pressure { get; set; }
            public double WindSpeed { get; set; }
            public double WindDirection { get; set; }
            public double WindGust { get; set; }
            public double CloudCover { get; set; }
            public double SkyTemperature { get; set; }
            public double SkyBrightness { get; set; }
            public double SkyQuality { get; set; }
            public string ExposureStartUTC { get; set; }

            public WeatherMetaDataRecord() {
            }

            public WeatherMetaDataRecord(ImageSavedEventArgs msg) {
                ExposureNumber = msg.MetaData.Image.ExposureNumber;
                ExposureStart = Utility.Utility.FormatDateTime(msg.MetaData.Image.ExposureStart);
                ExposureStartUTC = Utility.Utility.FormatDateTimeISO8601(msg.MetaData.Image.ExposureStart);
                WeatherDataParameter weatherData = msg.MetaData.WeatherData;
                Temperature = SafeRound(weatherData.Temperature, 1);
                DewPoint = SafeRound(weatherData.DewPoint, 1);
                Humidity = weatherData.Humidity;
                Pressure = weatherData.Pressure;
                WindSpeed = weatherData.WindSpeed;
                WindDirection = weatherData.WindDirection;
                WindGust = weatherData.WindGust;
                CloudCover = weatherData.CloudCover;
                SkyTemperature = SafeRound(weatherData.SkyTemperature, 1);
                SkyBrightness = weatherData.SkyBrightness;
                SkyQuality = weatherData.SkyQuality;
            }

            private double SafeRound(double value, int digits) {
                return (Double.IsNaN(value)) ? value : Math.Round(value, digits);
            }
        }

        public class AcquisitionMetaDataRecord {
            public string TargetName { get; }
            public string RACoordinates { get; }
            public string DECCoordinates { get; }
            public string TelescopeName { get; }
            public double FocalLength { get; }
            public double FocalRatio { get; }
            public string CameraName { get; }
            public double PixelSize { get; }
            public int BitDepth { get; }
            public double ObserverLatitude { get; }
            public double ObserverLongitude { get; }
            public double ObserverElevation { get; }

            public AcquisitionMetaDataRecord(ImageSavedEventArgs msg) {
                TargetName = msg.MetaData.Target.Name;
                RACoordinates = ReformatRA(msg.MetaData.Target.Coordinates?.RAString);
                DECCoordinates = ReformatDEC(msg.MetaData.Target.Coordinates?.DecString);
                TelescopeName = msg.MetaData.Telescope.Name;
                FocalLength = Utility.Utility.ReformatDouble(msg.MetaData.Telescope.FocalLength);
                FocalRatio = Utility.Utility.ReformatDouble(msg.MetaData.Telescope.FocalRatio);
                CameraName = msg.MetaData.Camera.Name;
                PixelSize = Utility.Utility.ReformatDouble(msg.MetaData.Camera.PixelSize);
                BitDepth = msg.Statistics.BitDepth;
                ObserverLatitude = Utility.Utility.ReformatDouble(msg.MetaData.Observer.Latitude);
                ObserverLongitude = Utility.Utility.ReformatDouble(msg.MetaData.Observer.Longitude);
                ObserverElevation = Utility.Utility.ReformatDouble(msg.MetaData.Observer.Elevation);
            }

            public string ReformatRA(string RAString) {
                try {
                    string pattern = @"(\d+):(\d+):(\d+)";
                    if (Regex.IsMatch(RAString, pattern)) {
                        Match match = Regex.Match(RAString, pattern);
                        return $"{Zeros(match.Groups[1].Value)}h {Zeros(match.Groups[2].Value)}m {Zeros(match.Groups[3].Value)}s";
                    } else {
                        return RAString;
                    }
                } catch (Exception) {
                    return "";
                }
            }

            private string Zeros(string value) {
                value = value.TrimStart('0');
                return (value == "") ? "0" : value;
            }

            public string ReformatDEC(string DECString) {
                return DECString != null ? DECString : "";
            }
        }

        public class Equipment {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Telescope { get; set; }
            public string Camera { get; set; }
            public string Focuser { get; set; }
            public string FilterWheel { get; set; }
            public string CaptureSoftware { get; set; }
            public string Hash { get; set; }

            public override string ToString() {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(Name)) parts.Add(Name);
                if (!string.IsNullOrEmpty(Telescope)) parts.Add(Telescope);
                if (!string.IsNullOrEmpty(Camera)) parts.Add(Camera);
                return parts.Count > 0 ? string.Join(" - ", parts) : $"Equipment {Id}";
            }
        }

        public class Location {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public double Longitude { get; set; }
            public double Latitude { get; set; }
            public double Altitude { get; set; }
            public string Timezone { get; set; }

            public override string ToString() {
                return !string.IsNullOrEmpty(Name) ? Name : $"Location {Id}";
            }
        }

        public class EquipmentListResponse {
            public bool Success { get; set; }
            public List<Equipment> Equipments { get; set; }
            public int Count { get; set; }
            public long Timestamp { get; set; }
        }

        public class LocationListResponse {
            public bool Success { get; set; }
            public List<Location> Locations { get; set; }
            public int Count { get; set; }
            public long Timestamp { get; set; }
        }

        public class ApiErrorResponse {
            public bool Success { get; set; }
            public string Error { get; set; }
            public string Message { get; set; }
            public long Timestamp { get; set; }
        }
    }
}