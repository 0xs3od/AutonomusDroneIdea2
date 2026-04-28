using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Linq; // أضف هذا السطر لضمان عمل عمليات القوائم

namespace DroneGCS
{
    // ════════════════════════════════════════════════
    // ENUMS & DATA MODELS
    // ════════════════════════════════════════════════

    public enum DronePhase
    {
        Idle,
        Patrol,
        Tracking,
        WireDeploy,
        WireFailed,
        Kamikaze,
        Intercepted,
        Returning
    }

    public enum ThreatLevel
    {
        None,
        Low,
        Medium,
        High,
        Critical
    }

    public class DroneState
    {
        public double X { get; set; }           // Map canvas X
        public double Y { get; set; }           // Map canvas Y
        public double Altitude { get; set; }    // meters
        public double Speed { get; set; }       // m/s
        public double Heading { get; set; }     // degrees
        public double Battery { get; set; }     // 0-100%
        public DronePhase Phase { get; set; }
        public bool KevlarReady { get; set; }
        public bool KamikazeMode { get; set; }
    }

    public class EnemyDroneState
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Speed { get; set; }       // m/s
        public double Heading { get; set; }
        public double ThermalTemp { get; set; } // °C
        public bool HasExplosives { get; set; }
        public bool IsDetected { get; set; }
        public bool IsIntercepted { get; set; }
        public string DroneType { get; set; }   // e.g. "SHAHED-136"
    }

    public class SensorData
    {
        public double LidarDistance { get; set; }   // meters
        public double AcousticDB { get; set; }       // dB
        public double ThermalTemp { get; set; }      // °C
        public double ConeAlignment { get; set; }    // 0-100%
        public double Roll { get; set; }
        public double Pitch { get; set; }
        public double Yaw { get; set; }
    }

    // ════════════════════════════════════════════════
    // MAIN WINDOW CODE-BEHIND
    // ════════════════════════════════════════════════

    public partial class MainWindow : Window
    {
        // ─── Timers ───────────────────────────────────
        private DispatcherTimer _simTimer;       // Main simulation tick (50ms)
        private DispatcherTimer _clockTimer;     // UI clock (1s)
        private DispatcherTimer _pidTimer;       // PID chart update (100ms)

        // ─── State ────────────────────────────────────
        private DroneState _drone = new DroneState();
        private EnemyDroneState _enemy = new EnemyDroneState();
        private SensorData _sensors = new SensorData();
        private bool _isManualMode = false;
        private bool _missionActive = true;
        private DateTime _missionStart;
        private int _simTick = 0;

        // ─── PID Chart Data ───────────────────────────
        private List<double> _rollHistory = new List<double>();
        private List<double> _pitchHistory = new List<double>();
        private List<double> _yawHistory = new List<double>();
        private const int PID_HISTORY_MAX = 100;

        // ─── Map Pan/Zoom ─────────────────────────────
        private double _mapScale = 1.0;
        private Point _mapOffset = new Point(0, 0);
        private bool _isPanning = false;
        private Point _panStart;
        private double _dronePatrolX = 150.0;
        private double _dronePatrolDir = 1.0; // 1 = right, -1 = left

        // ─── Mission Log ──────────────────────────────
        private ObservableCollection<string> _missionLog = new ObservableCollection<string>();

        // ─── Enemy Simulation ─────────────────────────
        private Random _rng = new Random();
        private bool _enemySpawned = false;
        private double _enemySpawnTimer = 0;
        private double _nextEnemySpawn = 15.0; // seconds until first enemy

        // ─── Patrol waypoints ────────────────────────
        private double[] _patrolWaypoints = { 150, 300, 450, 600, 750 };
        private int _currentWaypoint = 0;

        // ════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════

        public MainWindow()
        {
            InitializeComponent();
            InitializeState();
            InitializeTimers();

            MissionLogList.ItemsSource = _missionLog;
            _missionStart = DateTime.Now;

            LogMission("SYS", "System initialized. All sensors online.");
            LogMission("SYS", "Drone ALPHA-1 assigned to border sector 7.");
            LogMission("NAV", "Patrol route loaded. Waypoints: 5.");
            LogMission("SEN", "LiDAR active. Microphone array active.");
            LogMission("CAM", "Thermal camera: IR mode engaged.");
            LogMission("NET", "Comm link established. Ping: 12ms.");
        }

        private void InitializeState()
        {
            _drone.X = 150;
            _drone.Y = 400;
            _drone.Altitude = 120;
            _drone.Speed = 18.5;
            _drone.Heading = 90;
            _drone.Battery = 87;
            _drone.Phase = DronePhase.Patrol;
            _drone.KevlarReady = true;
            _drone.KamikazeMode = false;

            _enemy.IsDetected = false;
            _enemy.IsIntercepted = false;
            _enemy.DroneType = "UNKNOWN";
        }

        private void InitializeTimers()
        {
            // Main simulation
            _simTimer = new DispatcherTimer();
            _simTimer.Interval = TimeSpan.FromMilliseconds(50);
            _simTimer.Tick += SimTimer_Tick;
            _simTimer.Start();

            // Clock
            _clockTimer = new DispatcherTimer();
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += ClockTimer_Tick;
            _clockTimer.Start();

            // PID chart
            _pidTimer = new DispatcherTimer();
            _pidTimer.Interval = TimeSpan.FromMilliseconds(80);
            _pidTimer.Tick += PIDTimer_Tick;
            _pidTimer.Start();
        }

        // ════════════════════════════════════════════════
        // MAIN SIMULATION TICK (50ms)
        // ════════════════════════════════════════════════

        private void SimTimer_Tick(object sender, EventArgs e)
        {
            _simTick++;
            double dt = 0.05; // 50ms in seconds

            // Battery drain
            _drone.Battery = Math.Max(0, _drone.Battery - 0.002);

            // Enemy spawn logic
            if (!_enemySpawned)
            {
                _enemySpawnTimer += dt;
                if (_enemySpawnTimer >= _nextEnemySpawn)
                    SpawnEnemy();
            }

            if (!_isManualMode)
            {
                RunAutonomousLogic(dt);
            }

            UpdateEnemyMovement(dt);
            UpdateSensors();
            UpdateUI();
        }

        // ════════════════════════════════════════════════
        // AUTONOMOUS FLIGHT LOGIC
        // ════════════════════════════════════════════════

        private void RunAutonomousLogic(double dt)
        {
            if (_enemy.IsDetected && !_enemy.IsIntercepted &&
          _drone.Phase != DronePhase.Tracking &&
          _drone.Phase != DronePhase.WireDeploy &&
          _drone.Phase != DronePhase.Kamikaze)
            {
                _drone.Phase = DronePhase.Tracking;
                LogMission("TGT", "⚠ NEW TARGET DETECTED! Intercepting immediately.");
                SetPhaseUI(2);
            }

            switch (_drone.Phase)
            {
                case DronePhase.Patrol:
                    RunPatrol(dt);
                    if (_enemy.IsDetected)
                    {
                        _drone.Phase = DronePhase.Tracking;
                        LogMission("TGT", $"Target acquired! Switching to TRACK mode.");
                        LogMission("SEN", $"Acoustic signature: {_sensors.AcousticDB:F1} dB");
                        SetPhaseUI(2);
                    }
                    break;

                case DronePhase.Tracking:
                    RunTracking(dt);
                    // Check if close enough for wire deploy
                    double dist = GetDistanceToEnemy();
                    if (dist < 25 && _drone.KevlarReady)
                    {
                        _drone.Phase = DronePhase.WireDeploy;
                        LogMission("INT", "Entering wire deploy range. Positioning above target.");
                        SetPhaseUI(3);
                    }
                    break;

                case DronePhase.WireDeploy:
                    RunWireDeploy(dt);
                    break;

                case DronePhase.WireFailed:
                    // Wire failed — switch to Kamikaze
                    _drone.Phase = DronePhase.Kamikaze;
                    LogMission("WRN", "Wire deployment FAILED. Switching to KAMIKAZE mode.");
                    LogMission("KAM", "Targeting rotor assembly. Initiating collision run.");
                    SetPhaseUI(4);
                    EnableKamikazeVisuals();
                    break;

                case DronePhase.Kamikaze:
                    RunKamikaze(dt);
                    break;

                case DronePhase.Intercepted:
                    RunReturn(dt);
                    break;
            }
        }

        private void RunPatrol(double dt)
        {
            // Move along patrol waypoints
            double targetX = _patrolWaypoints[_currentWaypoint];
            double dx = targetX - _drone.X;

            if (Math.Abs(dx) < 5)
            {
                _currentWaypoint = (_currentWaypoint + 1) % _patrolWaypoints.Length;
            }
            else
            {
                _drone.X += (dx > 0 ? 1 : -1) * _drone.Speed * dt * 0.8;
                _drone.Heading = dx > 0 ? 90 : 270;
            }

            // Gentle altitude variation
            _drone.Altitude = 120 + Math.Sin(_simTick * 0.05) * 5;
            _drone.Speed = 18.5 + Math.Sin(_simTick * 0.03) * 1.5;
        }

        private void RunTracking(double dt)
        {
            // Proportional Navigation toward enemy
            double dx = _enemy.X - _drone.X;
            double dy = _enemy.Y - _drone.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);

            if (dist > 0)
            {
                double speed = Math.Min(28, _drone.Speed + 2); // Accelerate
                _drone.X += (dx / dist) * speed * dt * 0.9;
                _drone.Y += (dy / dist) * speed * dt * 0.9;
                _drone.Speed = speed;
                _drone.Heading = Math.Atan2(dy, dx) * 180 / Math.PI + 90;
            }

            // Rise above target (Top-Down approach)
            _drone.Altitude = Math.Min(180, _drone.Altitude + 2 * dt * 10);
        }

        private void RunWireDeploy(double dt)
        {
            // Position directly above enemy (suction cone)
            double dx = _enemy.X - _drone.X;
            double dy = (_enemy.Y - 20) - _drone.Y; // 20px above
            _drone.X += dx * 1;
            _drone.Y += dy * 1;

            // Check alignment quality
            double alignErr = Math.Sqrt(dx * dx + dy * dy);
            _sensors.ConeAlignment = Math.Max(0, 100 - alignErr * 8);

            // Wire deploy trigger
            if (_sensors.ConeAlignment > 85 && _simTick % 40 == 0)
            {
                bool wireSuccess = _rng.NextDouble() > 0.3; // 70% success
                if (wireSuccess)
                {
                    InterceptEnemy();
                }
                else
                {
                    LogMission("WRN", "Wire miss! Realigning...");
                }
            }
        }

        private void RunKamikaze(double dt)
        {
            // Direct collision course with enemy rotor
            double dx = _enemy.X - _drone.X;
            double dy = _enemy.Y - _drone.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);

            if (dist > 0)
            {
                double speed = 45; // Full speed
                _drone.X += (dx / dist) * speed * dt;
                _drone.Y += (dy / dist) * speed * dt;
                _drone.Speed = speed;
            }

            if (dist < 8)
            {
                InterceptEnemy();
                LogMission("KAM", "DIRECT HIT — Rotor assembly destroyed.");
            }
        }

        private void RunReturn(double dt)
        {
            // Return to patrol start
            double dx = 150 - _drone.X;
            double dy = 400 - _drone.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > 5)
            {
                _drone.X += (dx / dist) * 15 * dt;
                _drone.Y += (dy / dist) * 15 * dt;
            }
            else
            {
                _drone.Phase = DronePhase.Patrol;
                LogMission("NAV", "Returning to patrol. Standing by.");
                SetPhaseUI(1);
            }
        }

        // ════════════════════════════════════════════════
        // RESPAWN NEW INTERCEPTOR
        // ════════════════════════════════════════════════


        private void RespawnInterceptor()
        {
            // إعادة الدرون إلى إحداثيات القاعدة (نقطة البداية)
            _drone.X = 100;
            _drone.Y = 400;
            _drone.Battery = 100;
            _drone.Phase = DronePhase.Idle;
            _drone.KevlarReady = true;
            _drone.KamikazeMode = false;

            LogMission("SYS", "⚡ NEW INTERCEPTOR DEPLOYED FROM BASE.");

            // تحديث الواجهة فوراً ليظهر في موقعه الجديد
            UpdateUI();
        }


        // ════════════════════════════════════════════════
        // ENEMY SIMULATION
        // ════════════════════════════════════════════════

        private void SpawnEnemy()
        {
            _enemySpawned = true;
            _enemy.IsIntercepted = false;
            _enemy.IsDetected = true;
            _enemy.X = 700 + _rng.NextDouble() * 50;
            _enemy.Y = 100 + _rng.NextDouble() * 40;
            _enemy.Speed = 12 + _rng.NextDouble() * 8;
            _enemy.Heading = 180 + _rng.NextDouble() * 30 - 15;
            _enemy.HasExplosives = _rng.NextDouble() > 0.3; // 70% have explosives
            _enemy.ThermalTemp = _enemy.HasExplosives ?
                320 + _rng.NextDouble() * 80 :
                150 + _rng.NextDouble() * 60;
            _enemy.DroneType = new[] { "SHAHED-136", "MOHAJER-6", "UNKNOWN-UAS" }[_rng.Next(3)];
            _enemy.IsDetected = true;

            LogMission("ALT", $"⚠ HOSTILE DETECTED: {_enemy.DroneType}");
            LogMission("ALT", $"Thermal: {_enemy.ThermalTemp:F0}°C | Explosives: {(_enemy.HasExplosives ? "YES" : "NO")}");
            LogMission("ALT", $"Entry vector: {_enemy.Heading:F0}° | Speed: {_enemy.Speed:F1} m/s");

            // Update enemy UI
            Dispatcher.Invoke(() =>
            {
                EnemyDroneCanvas.Visibility = Visibility.Visible;
                EnemyTypeLabel.Text = _enemy.DroneType;
                ThreatLevel.Text = _enemy.HasExplosives ? "CRITICAL" : "HIGH";
                ThreatLevel.Foreground = _enemy.HasExplosives ?
                    new SolidColorBrush(Color.FromRgb(255, 59, 59)) :
                    new SolidColorBrush(Color.FromRgb(255, 214, 0));
                TwinEnemyDrone.Visibility = Visibility.Visible;
                NoSignalOverlay.Visibility = Visibility.Collapsed;
                ThermalHotspot.Visibility = Visibility.Visible;
                ThermalHotspot.Opacity = 1;

                if (_enemy.HasExplosives)
                    ExplosiveWarning.Visibility = Visibility.Visible;

                // Enable action buttons (manual override)
                BtnEngage.IsEnabled = true;
                BtnDeployWire.IsEnabled = true;
                BtnKamikaze.IsEnabled = true;
            });
        }

        private void UpdateEnemyMovement(double dt)
        {
            if (!_enemy.IsDetected || _enemy.IsIntercepted) return;

            // Fly pre-programmed path (inertial navigation simulation)
            double headingRad = (_enemy.Heading - 90) * Math.PI / 180;
            _enemy.X -= Math.Cos(headingRad) * _enemy.Speed * dt * 0.5;
            _enemy.Y += Math.Sin(headingRad) * _enemy.Speed * dt * 0.5 + 0.3;

            // Slight oscillation
            _enemy.X += Math.Sin(_simTick * 0.1) * 0.3;
        }

        private void InterceptEnemy()
        {
            _enemy.IsIntercepted = true;
            _drone.Phase = DronePhase.Intercepted;
            _enemySpawned = false;
            _nextEnemySpawn = 10;
            //20 + _rng.NextDouble() * 30;
            _enemySpawnTimer = 0;


            _drone.KevlarReady = true;
            _drone.KamikazeMode = false;

            LogMission("INT", "✓ TARGET INTERCEPTED SUCCESSFULLY");
            LogMission("INT", $"Method: {(_drone.KamikazeMode ? "Rotor RAM" : "Kevlar Wire")}");
            LogMission("SYS", "Resetting for next patrol cycle.");

            Dispatcher.Invoke(() =>
            {
                // Flash intercept
                EnemyDroneCanvas.Visibility = Visibility.Collapsed;
                TwinEnemyDrone.Visibility = Visibility.Collapsed;
                InterceptLine.Visibility = Visibility.Collapsed;
                ThermalHotspot.Visibility = Visibility.Collapsed;
                ExplosiveWarning.Visibility = Visibility.Collapsed;
                SuctionCone.Visibility = Visibility.Collapsed;
                TwinWireLine.Visibility = Visibility.Collapsed;
                NoSignalOverlay.Visibility = Visibility.Visible;
                ThreatLevel.Text = "NONE";
                ThreatLevel.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 136));
                BtnEngage.IsEnabled = false;
                BtnDeployWire.IsEnabled = false;
                BtnKamikaze.IsEnabled = false;
                _drone.KamikazeMode = false;
                _drone.KevlarReady = true;
                _enemy.IsDetected = false;
                SetPhaseUI(1);
            });

            if (_drone.Phase == DronePhase.Kamikaze)
            {
                LogMission("CRT", "💥 BOOM! KAMIKAZE STRIKE SUCCESSFUL.");

                RespawnInterceptor();
            }
            else
            {
                _drone.Phase = DronePhase.Intercepted;
                LogMission("INT", "✓ Target neutralized by wire. Returning to base.");
            }

            _enemySpawnTimer = 0;
        }

        // ════════════════════════════════════════════════
        // SENSOR SIMULATION
        // ════════════════════════════════════════════════

        private void UpdateSensors()
        {
            if (_enemy.IsDetected && !_enemy.IsIntercepted)
            {
                double dist = GetDistanceToEnemy();
                _sensors.LidarDistance = dist * 1.2; // scale to meters
                _sensors.AcousticDB = Math.Max(20, 85 - dist * 0.8) + _rng.NextDouble() * 3;
                _sensors.ThermalTemp = _enemy.ThermalTemp + _rng.NextDouble() * 10;
            }
            else
            {
                _sensors.LidarDistance = double.NaN;
                _sensors.AcousticDB = 22 + _rng.NextDouble() * 5; // ambient noise
                _sensors.ThermalTemp = double.NaN;
                _sensors.ConeAlignment = 0;
            }

            // PID simulation (drone stabilization with noise)
            double stability = _drone.Phase == DronePhase.WireDeploy ? 0.4 : 0.15;
            _sensors.Roll = Math.Sin(_simTick * 0.08) * stability + (_rng.NextDouble() - 0.5) * 0.1;
            _sensors.Pitch = Math.Cos(_simTick * 0.06) * stability + (_rng.NextDouble() - 0.5) * 0.1;
            _sensors.Yaw = Math.Sin(_simTick * 0.04 + 1) * stability * 0.5 + (_rng.NextDouble() - 0.5) * 0.05;
        }

        private double GetDistanceToEnemy()
        {
            double dx = _enemy.X - _drone.X;
            double dy = _enemy.Y - _drone.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ════════════════════════════════════════════════
        // UI UPDATE
        // ════════════════════════════════════════════════

        private void UpdateUI()
        {
            // --- Drone canvas position ---
            Canvas.SetLeft(OurDroneCanvas, _drone.X);
            Canvas.SetTop(OurDroneCanvas, _drone.Y);
            DroneRotation.Angle = _drone.Heading - 90;

            // --- Enemy canvas position ---
            if (_enemy.IsDetected && !_enemy.IsIntercepted)
            {
                Canvas.SetLeft(EnemyDroneCanvas, _enemy.X);
                Canvas.SetTop(EnemyDroneCanvas, _enemy.Y);
                Canvas.SetLeft(TwinEnemyDrone, 140 + (_enemy.X - _drone.X) * 0.4);
                Canvas.SetTop(TwinEnemyDrone, 70 + (_enemy.Y - _drone.Y) * 0.3);

                // Intercept line
                if (_drone.Phase == DronePhase.Tracking || _drone.Phase == DronePhase.WireDeploy || _drone.Phase == DronePhase.Kamikaze)
                {
                    InterceptLine.Visibility = Visibility.Visible;
                    InterceptLine.X1 = _drone.X;
                    InterceptLine.Y1 = _drone.Y;
                    InterceptLine.X2 = _enemy.X;
                    InterceptLine.Y2 = _enemy.Y;
                    InterceptLine.Stroke = _drone.Phase == DronePhase.Kamikaze ?
                        new SolidColorBrush(Color.FromRgb(255, 59, 59)) :
                        new SolidColorBrush(Color.FromRgb(255, 214, 0));
                }

                // Suction cone & wire visuals
                if (_drone.Phase == DronePhase.WireDeploy)
                {
                    SuctionCone.Visibility = Visibility.Visible;
                    TwinWireLine.Visibility = Visibility.Visible;
                    Canvas.SetLeft(WireCanvas, _drone.X);
                    Canvas.SetTop(WireCanvas, _drone.Y);
                    WireCanvas.Visibility = Visibility.Visible;
                }

                // Thermal hotspot position
                Canvas.SetLeft(ThermalHotspot, 100 + (_enemy.X - _drone.X) * 0.3);
                Canvas.SetTop(ThermalHotspot, 60 + (_enemy.Y - _drone.Y) * 0.3);
            }

            // --- Left panel: Drone Status ---
            DroneState.Text = _drone.Phase.ToString().ToUpper();
            DroneState.Foreground = _drone.Phase switch
            {
                DronePhase.Patrol => new SolidColorBrush(Color.FromRgb(0, 255, 136)),
                DronePhase.Tracking => new SolidColorBrush(Color.FromRgb(255, 214, 0)),
                DronePhase.WireDeploy => new SolidColorBrush(Color.FromRgb(0, 212, 255)),
                DronePhase.Kamikaze => new SolidColorBrush(Color.FromRgb(255, 59, 59)),
                _ => new SolidColorBrush(Color.FromRgb(90, 106, 138))
            };

            DroneAltitude.Text = $"{_drone.Altitude:F0} m";
            DroneSpeed.Text = $"{_drone.Speed:F1} m/s";

            // Battery bar
            double battW = (_drone.Battery / 100.0) * 55;
            BatteryFill.Width = Math.Max(0, battW);
            BatteryFill.Fill = _drone.Battery > 50
                ? new SolidColorBrush(Color.FromRgb(0, 255, 136))
                : _drone.Battery > 25
                    ? new SolidColorBrush(Color.FromRgb(255, 214, 0))
                    : new SolidColorBrush(Color.FromRgb(255, 59, 59));
            BatteryPct.Text = $" {_drone.Battery:F0}%";

            // --- Targeting Data ---
            if (_enemy.IsDetected && !_enemy.IsIntercepted)
            {
                double dist = GetDistanceToEnemy() * 1.2;
                TargetDistance.Text = $"{dist:F1} m";
                RelativeVelocity.Text = $"{Math.Abs(_enemy.Speed - _drone.Speed):F1} m/s";
                double angle = Math.Atan2(_enemy.Y - _drone.Y, _enemy.X - _drone.X) * 180 / Math.PI;
                AttackAngle.Text = $"{angle:F1}°";
                LidarRange.Text = $"{_sensors.LidarDistance:F1} m";
                AcousticSig.Text = $"{_sensors.AcousticDB:F1} dB";
                ExplosiveRisk.Text = _enemy.HasExplosives ? "عالي جداً!" : "منخفض";
                ExplosiveRisk.Foreground = _enemy.HasExplosives
                    ? new SolidColorBrush(Color.FromRgb(255, 59, 59))
                    : new SolidColorBrush(Color.FromRgb(0, 255, 136));
                ThermalTemp.Text = $"{_sensors.ThermalTemp:F0}°C";
            }
            else
            {
                TargetDistance.Text = "---";
                RelativeVelocity.Text = "---";
                AttackAngle.Text = "---";
                LidarRange.Text = "---";
                AcousticSig.Text = "---";
                ThermalTemp.Text = "---°C";
            }

            // --- System Status ---
            KevlarStatus.Text = _drone.KevlarReady ? "● READY" : "○ DEPLOYED";
            KevlarStatus.Foreground = _drone.KevlarReady
                ? new SolidColorBrush(Color.FromRgb(0, 255, 136))
                : new SolidColorBrush(Color.FromRgb(255, 214, 0));
            KamikazeStatus.Text = _drone.KamikazeMode ? "● ARMED" : "○ STANDBY";
            KamikazeStatus.Foreground = _drone.KamikazeMode
                ? new SolidColorBrush(Color.FromRgb(255, 59, 59))
                : new SolidColorBrush(Color.FromRgb(90, 106, 138));

            // --- Sensor Matrix (right panel) ---
            LidarValue.Text = double.IsNaN(_sensors.LidarDistance) ? "---" : $"{_sensors.LidarDistance:F1}";
            AcousticValue.Text = $"{_sensors.AcousticDB:F1}";
            ThermalValue.Text = double.IsNaN(_sensors.ThermalTemp) ? "---°C" : $"{_sensors.ThermalTemp:F0}°C";
            ThermalValue.Foreground = _enemy.IsDetected && !_enemy.IsIntercepted
                ? (_enemy.HasExplosives
                    ? new SolidColorBrush(Color.FromRgb(255, 59, 59))
                    : new SolidColorBrush(Color.FromRgb(255, 214, 0)))
                : new SolidColorBrush(Color.FromRgb(255, 214, 0));
            ThermalRiskLabel.Text = _enemy.IsDetected
                ? (_enemy.HasExplosives ? "⚠ EXPLOSIVE RISK" : "low risk")
                : "no target";
            ConeAlign.Text = _drone.Phase == DronePhase.WireDeploy
                ? $"{_sensors.ConeAlignment:F0}%"
                : "---% ";

            // --- Digital Twin labels ---
            TwinAlt.Text = $"{_drone.Altitude:F0}m";
            TwinHdg.Text = $"{_drone.Heading:F0}°";
            TwinSpd.Text = $"{_drone.Speed:F1}m/s";

            // Mode text
            ModeText.Text = _isManualMode
                ? "MANUAL CONTROL"
                : $"AUTO — {_drone.Phase.ToString().ToUpper()}";

            // Status bar
            StatusBarText.Text = GetStatusText();
        }

        private string GetStatusText()
        {
            return _drone.Phase switch
            {
                DronePhase.Patrol => "● SYSTEM NOMINAL — Drone ALPHA-1 on patrol. Scanning border zone.",
                DronePhase.Tracking => $"⚠ TARGET TRACKED — {_enemy.DroneType} | Closing distance: {GetDistanceToEnemy() * 1.2:F0}m",
                DronePhase.WireDeploy => $"🔗 WIRE DEPLOY — Cone alignment: {_sensors.ConeAlignment:F0}% | Positioning above target rotor.",
                DronePhase.Kamikaze => "💥 KAMIKAZE RUN — Full throttle. Targeting rotor assembly.",
                DronePhase.Intercepted => "✓ INTERCEPT SUCCESS — Target neutralized. Returning to patrol.",
                _ => "● STANDBY"
            };
        }

        // ════════════════════════════════════════════════
        // PID CHART UPDATE
        // ════════════════════════════════════════════════

        private void PIDTimer_Tick(object sender, EventArgs e)
        {
            _rollHistory.Add(_sensors.Roll);
            _pitchHistory.Add(_sensors.Pitch);
            _yawHistory.Add(_sensors.Yaw);

            if (_rollHistory.Count > PID_HISTORY_MAX) _rollHistory.RemoveAt(0);
            if (_pitchHistory.Count > PID_HISTORY_MAX) _pitchHistory.RemoveAt(0);
            if (_yawHistory.Count > PID_HISTORY_MAX) _yawHistory.RemoveAt(0);

            DrawPIDChart();
        }

        private void DrawPIDChart()
        {
            double w = PIDCanvas.ActualWidth;
            double h = PIDCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            double midY = h / 2;
            double scaleY = h / 4.0; // ±2 range maps to full height
            double stepX = w / PID_HISTORY_MAX;

            PIDRollLine.Points = BuildPoints(_rollHistory, stepX, midY, scaleY);
            PIDPitchLine.Points = BuildPoints(_pitchHistory, stepX, midY, scaleY);
            PIDYawLine.Points = BuildPoints(_yawHistory, stepX, midY, scaleY);
        }

        private PointCollection BuildPoints(List<double> data, double stepX, double midY, double scaleY)
        {
            var pts = new PointCollection();
            for (int i = 0; i < data.Count; i++)
                pts.Add(new Point(i * stepX, midY - data[i] * scaleY));
            return pts;
        }

        // ════════════════════════════════════════════════
        // CLOCK TIMER
        // ════════════════════════════════════════════════

        private void ClockTimer_Tick(object sender, EventArgs e)
        {
            var elapsed = DateTime.Now - _missionStart;
            MissionClock.Text = elapsed.ToString(@"hh\:mm\:ss");
            SimTime.Text = elapsed.ToString(@"hh\:mm\:ss");
            PingMs.Text = $"{10 + _rng.Next(8)}ms  ";
        }

        // ════════════════════════════════════════════════
        // PHASE UI HELPER
        // ════════════════════════════════════════════════

        private void SetPhaseUI(int active)
        {
            var phases = new[]
            {
                (Phase1Border, (TextBlock)null),
                (Phase2Border, Phase2Text),
                (Phase3Border, Phase3Text),
                (Phase4Border, Phase4Text)
            };

            for (int i = 0; i < phases.Length; i++)
            {
                bool isActive = (i + 1) == active;
                bool isDone = (i + 1) < active;

                if (isActive)
                {
                    phases[i].Item1.Background = new SolidColorBrush(Color.FromRgb(0, 212, 255));
                    phases[i].Item1.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 212, 255));
                    if (phases[i].Item2 != null)
                        phases[i].Item2.Foreground = new SolidColorBrush(Color.FromRgb(10, 14, 26));
                }
                else if (isDone)
                {
                    phases[i].Item1.Background = new SolidColorBrush(Color.FromRgb(0, 64, 40));
                    phases[i].Item1.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 255, 136));
                    if (phases[i].Item2 != null)
                        phases[i].Item2.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 136));
                }
                else
                {
                    phases[i].Item1.Background = new SolidColorBrush(Color.FromRgb(20, 28, 53));
                    phases[i].Item1.BorderBrush = new SolidColorBrush(Color.FromRgb(26, 48, 96));
                    if (phases[i].Item2 != null)
                        phases[i].Item2.Foreground = new SolidColorBrush(Color.FromRgb(90, 106, 138));
                }
            }
        }

        private void EnableKamikazeVisuals()
        {
            _drone.KamikazeMode = true;
            _drone.KevlarReady = false;
            ModeIndicator.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 59, 59));
            ModeLight.Fill = new SolidColorBrush(Color.FromRgb(255, 59, 59));
        }

        // ════════════════════════════════════════════════
        // MISSION LOG
        // ════════════════════════════════════════════════

        private void LogMission(string tag, string msg)
        {
            var elapsed = DateTime.Now - _missionStart;
            string line = $"[{elapsed:mm\\:ss}] [{tag}] {msg}";
            Dispatcher.Invoke(() =>
            {
                _missionLog.Insert(0, line); // newest first
                if (_missionLog.Count > 50) _missionLog.RemoveAt(_missionLog.Count - 1);
            });
        }

        // ════════════════════════════════════════════════
        // BUTTON HANDLERS
        // ════════════════════════════════════════════════

        private void BtnToggleMode_Click(object sender, RoutedEventArgs e)
        {
            _isManualMode = !_isManualMode;
            BtnToggleMode.Content = _isManualMode ? "⚙ AUTO MODE" : "⚡ MANUAL OVERRIDE";
            BtnToggleMode.BorderBrush = _isManualMode
                ? new SolidColorBrush(Color.FromRgb(255, 214, 0))
                : new SolidColorBrush(Color.FromRgb(0, 212, 255));
            BtnToggleMode.Foreground = _isManualMode
                ? new SolidColorBrush(Color.FromRgb(255, 214, 0))
                : new SolidColorBrush(Color.FromRgb(0, 212, 255));
            LogMission("CTL", _isManualMode ? "Manual override activated." : "Autonomous mode restored.");
        }

        private void BtnAbort_Click(object sender, RoutedEventArgs e)
        {
            _simTimer.Stop();
            _drone.Phase = DronePhase.Returning;
            LogMission("ABT", "⚠ ABORT ALL — Mission halted by operator.");
            MessageBox.Show("Mission Aborted!\nAll autonomous operations stopped.", "ABORT", MessageBoxButton.OK, MessageBoxImage.Warning);
            _simTimer.Start();
        }

        private void BtnEngage_Click(object sender, RoutedEventArgs e)
        {
            if (_enemy.IsDetected && !_enemy.IsIntercepted)
            {
                _drone.Phase = DronePhase.Tracking;
                _isManualMode = false;
                LogMission("CTL", "Manual ENGAGE command. Switching to tracking.");
                SetPhaseUI(2);
            }
        }

        private void BtnDeployWire_Click(object sender, RoutedEventArgs e)
        {
            if (_enemy.IsDetected && !_enemy.IsIntercepted)
            {
                _drone.Phase = DronePhase.WireDeploy;
                _isManualMode = false;
                LogMission("CTL", "Manual WIRE DEPLOY command.");
                SetPhaseUI(3);
            }
        }

        private void BtnKamikaze_Click(object sender, RoutedEventArgs e)
        {
            if (_enemy.IsDetected && !_enemy.IsIntercepted)
            {
                var result = MessageBox.Show(
                    "تفعيل وضع الاصطدام المباشر؟\nهذا سيدمر الدرون المعترض.\n\nActivate Kamikaze mode?",
                    "⚠ KAMIKAZE CONFIRM",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    _drone.Phase = DronePhase.Kamikaze;
                    _isManualMode = false;
                    EnableKamikazeVisuals();
                    LogMission("KAM", "Kamikaze mode confirmed by operator.");
                    SetPhaseUI(4);
                }
            }
        }

        // ════════════════════════════════════════════════
        // MAP INTERACTIONS (Pan / Zoom)
        // ════════════════════════════════════════════════

        private void MapCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isPanning = true;
            _panStart = e.GetPosition(MapCanvas);
            MapCanvas.CaptureMouse();
        }

        private void MapCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                Point current = e.GetPosition(MapCanvas);
                double dx = current.X - _panStart.X;
                double dy = current.Y - _panStart.Y;

                // Shift all canvas children
                foreach (UIElement child in MapCanvas.Children)
                {
                    if (child is FrameworkElement fe)
                    {
                        double left = Canvas.GetLeft(fe);
                        double top = Canvas.GetTop(fe);
                        if (!double.IsNaN(left)) Canvas.SetLeft(fe, left + dx * 0.1);
                        if (!double.IsNaN(top)) Canvas.SetTop(fe, top + dy * 0.1);
                    }
                }
                _panStart = current;
            }
        }

        private void MapCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isPanning = false;
            MapCanvas.ReleaseMouseCapture();
        }

        private void MapCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _mapScale += e.Delta > 0 ? 0.1 : -0.1;
            _mapScale = Math.Max(0.5, Math.Min(3.0, _mapScale));
        }

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => _mapScale = Math.Min(3.0, _mapScale + 0.2);
        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => _mapScale = Math.Max(0.5, _mapScale - 0.2);
        private void BtnCenter_Click(object sender, RoutedEventArgs e)
        {
            Canvas.SetLeft(OurDroneCanvas, 300);
            Canvas.SetTop(OurDroneCanvas, 380);
        }


    }
}
