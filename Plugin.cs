using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EasyBuildCam
{
    /// <summary>
    /// Free-fly build camera. Toggle with a key (default F6); while active the character stands still and the
    /// camera is flown with WASD / mouse. Numpad +/- change the speed. Client side only.
    /// </summary>
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGuid = "com.morda.easybuildcam";
        public const string ModName = "EasyBuildCam";
        public const string ModVersion = "1.0.0";

        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static ConfigEntry<KeyCode> FasterKey;
        internal static ConfigEntry<KeyCode> SlowerKey;
        internal static ConfigEntry<KeyCode> UpKey;
        internal static ConfigEntry<KeyCode> DownKey;
        internal static ConfigEntry<KeyCode> BoostKey;
        internal static ConfigEntry<float> Speed;
        internal static ConfigEntry<float> SpeedStep;
        internal static ConfigEntry<float> BoostMultiplier;
        internal static ConfigEntry<float> MouseSensitivity;

        internal static bool Active;
        private static float _origPlaceDistance = -1f;
        internal const float HugeRange = 100000f;
        private static float _yaw, _pitch;
        private static Vector3 _pos;

        private void Awake()
        {
            ToggleKey = Config.Bind("Keys", "Toggle", KeyCode.F6, "Turn the build camera on or off.");
            FasterKey = Config.Bind("Keys", "Faster", KeyCode.KeypadPlus, "Increase the flying speed.");
            SlowerKey = Config.Bind("Keys", "Slower", KeyCode.KeypadMinus, "Decrease the flying speed.");
            UpKey = Config.Bind("Keys", "Up", KeyCode.Space, "Fly up.");
            DownKey = Config.Bind("Keys", "Down", KeyCode.LeftControl, "Fly down.");
            BoostKey = Config.Bind("Keys", "Boost", KeyCode.LeftShift, "Hold to fly faster.");
            Speed = Config.Bind("Camera", "Speed", 10f, new ConfigDescription("Flying speed in meters per second.", new AcceptableValueRange<float>(0.5f, 500f)));
            SpeedStep = Config.Bind("Camera", "SpeedStep", 1.25f, "Each +/- press multiplies / divides the speed by this.");
            BoostMultiplier = Config.Bind("Camera", "BoostMultiplier", 3f, "Speed multiplier while the boost key is held.");
            MouseSensitivity = Config.Bind("Camera", "MouseSensitivity", 2f, "Mouse look sensitivity.");

            new Harmony(ModGuid).PatchAll(typeof(Plugin).Assembly);
        }

        private static bool UiBlocksInput()
        {
            return (Chat.instance != null && Chat.instance.HasFocus())
                || Console.IsVisible() || Menu.IsVisible() || TextInput.IsVisible()
                || (InventoryGui.instance != null && InventoryGui.IsVisible())
                || Hud.IsPieceSelectionVisible();
        }

        private void Update()
        {
            if (Player.m_localPlayer == null)
            {
                Active = false;
                _origPlaceDistance = -1f;
                return;
            }
            // Only usable with the build tool in hand; putting it away ends the camera.
            if (Active && !Player.m_localPlayer.InPlaceMode())
            {
                Active = false;
                SetPlaceDistance(Player.m_localPlayer, false);
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Build Cam OFF");
            }
            bool uiBlocked = UiBlocksInput();
            if (uiBlocked)
                return;

            if (Input.GetKeyDown(ToggleKey.Value) && (Active || Player.m_localPlayer.InPlaceMode()))
            {
                Active = !Active;
                if (Active && GameCamera.instance != null)
                {
                    var t = GameCamera.instance.transform;
                    _pos = t.position;
                    var e = t.rotation.eulerAngles;
                    _yaw = e.y;
                    _pitch = e.x > 180f ? e.x - 360f : e.x;
                }
                SetPlaceDistance(Player.m_localPlayer, Active);
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, Active ? "Build Cam ON" : "Build Cam OFF");
            }

            if (!Active)
                return;

            bool faster = Input.GetKeyDown(FasterKey.Value);
            bool slower = Input.GetKeyDown(SlowerKey.Value);
            if (faster)
                Speed.Value = Mathf.Min(500f, Speed.Value * SpeedStep.Value);
            if (slower)
                Speed.Value = Mathf.Max(0.5f, Speed.Value / SpeedStep.Value);
            if (faster || slower)
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "Cam speed: " + Speed.Value.ToString("0.0") + " m/s");

            _yaw += Input.GetAxisRaw("Mouse X") * MouseSensitivity.Value;
            _pitch = Mathf.Clamp(_pitch - Input.GetAxisRaw("Mouse Y") * MouseSensitivity.Value, -89f, 89f);
            var rot = Quaternion.Euler(_pitch, _yaw, 0f);

            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) move += Vector3.forward;
            if (Input.GetKey(KeyCode.S)) move += Vector3.back;
            if (Input.GetKey(KeyCode.D)) move += Vector3.right;
            if (Input.GetKey(KeyCode.A)) move += Vector3.left;
            if (Input.GetKey(UpKey.Value)) move += Vector3.up;
            if (Input.GetKey(DownKey.Value)) move += Vector3.down;

            float speed = Speed.Value * (Input.GetKey(BoostKey.Value) ? BoostMultiplier.Value : 1f);
            // Horizontal input follows the view, up/down stays world-aligned.
            Vector3 world = rot * new Vector3(move.x, 0f, move.z) + Vector3.up * move.y;
            _pos += world * (speed * Time.unscaledDeltaTime);
        }

        // The game measures the build / remove range from the character's eyes. While flying we lift that limit; the
        // camera ray itself is still limited to 50 m.
        private static void SetPlaceDistance(Player player, bool on)
        {
            if (on)
            {
                _origPlaceDistance = player.m_maxPlaceDistance;
                player.m_maxPlaceDistance = HugeRange;
            }
            else if (_origPlaceDistance > 0f)
            {
                player.m_maxPlaceDistance = _origPlaceDistance;
                _origPlaceDistance = -1f;
            }
        }

        internal static void Apply(Transform cam)
        {
            cam.position = _pos;
            cam.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }
    }

    [HarmonyPatch(typeof(GameCamera), "LateUpdate")]
    internal static class GameCamera_LateUpdate
    {
        private static void Postfix(GameCamera __instance)
        {
            if (Plugin.Active)
                Plugin.Apply(__instance.transform);
        }
    }

    // PlayerController reads walking and looking; blocking it keeps the character still. Player.TakeInput (building,
    // hotkeys) stays untouched, so the hammer keeps working.
    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    internal static class PlayerController_TakeInput
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Plugin.Active)
                return true;
            __result = false;
            return false;
        }
    }

    // Mouse movement must not turn the character while the camera flies.
    [HarmonyPatch(typeof(Player), nameof(Player.SetMouseLook))]
    internal static class Player_SetMouseLook
    {
        private static bool Prefix() => !Plugin.Active;
    }
}

namespace EasyBuildCam
{
    // Other mods or the game may put the range back after a placement; enforce it right before every range check.
    [HarmonyPatch]
    internal static class Player_RangeEnforce
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var name in new[] { "PieceRayTest", "UpdatePlacementGhost", "RemovePiece", "CopyPiece", "UpdateWearNTearHover" })
                yield return AccessTools.Method(typeof(Player), name);
        }

        [HarmonyPriority(Priority.Last)]
        private static void Prefix(Player __instance)
        {
            if (Plugin.Active && __instance == Player.m_localPlayer)
                __instance.m_maxPlaceDistance = Plugin.HugeRange;
        }
    }
}
