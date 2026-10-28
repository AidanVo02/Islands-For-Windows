using System;
using Windows.System.Power;

namespace DynamicIsland.Services
{
    public class BatteryInfo
    {
        public int Percentage { get; set; } = 100;
        public bool IsCharging { get; set; }
        public bool HasBattery { get; set; } = true;
        public string StatusText { get; set; } = "100%";
    }

    public class BatteryService
    {
        public event Action<BatteryInfo>? BatteryChanged;
        public BatteryInfo CurrentBattery { get; private set; } = new();

        public void Initialize()
        {
            try
            {
                UpdateBatteryInfo();

                PowerManager.BatteryStatusChanged += (s, e) => UpdateBatteryInfo();
                PowerManager.RemainingChargePercentChanged += (s, e) => UpdateBatteryInfo();
                PowerManager.PowerSupplyStatusChanged += (s, e) => UpdateBatteryInfo();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BatteryService error: {ex.Message}");
            }
        }

        private void UpdateBatteryInfo()
        {
            try
            {
                var status = PowerManager.BatteryStatus;
                int percent = PowerManager.RemainingChargePercent;
                bool isCharging = status == BatteryStatus.Charging;
                bool hasBattery = status != BatteryStatus.NotPresent;

                CurrentBattery = new BatteryInfo
                {
                    Percentage = percent,
                    IsCharging = isCharging,
                    HasBattery = hasBattery,
                    StatusText = isCharging ? $"Charging {percent}%" : $"{percent}%"
                };

                BatteryChanged?.Invoke(CurrentBattery);
            }
            catch
            {
                // Máy bàn PC không có pin hoặc ảo hóa
                CurrentBattery = new BatteryInfo
                {
                    HasBattery = false,
                    Percentage = 100,
                    IsCharging = false,
                    StatusText = "AC Power"
                };
            }
        }
    }
}
