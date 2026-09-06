using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>What a menu entry does when it is confirmed.</summary>
public enum PauseMenuAction
{
    Resume,
    Restart,
    Controls,
    Quit
}

[System.Serializable]
public struct PauseMenuEntryConfig
{
    public PauseMenuAction action;

    [Tooltip("Text shown in the list. Letter spacing is added automatically.")]
    public string label;

    [Tooltip("Small caption on the right of the row.")]
    public string caption;

    [Tooltip("Draw this entry in the danger colour when selected.")]
    public bool danger;
}

/// <summary>
/// Drop this on any GameObject in the racing scene and press Esc (or Start on a
/// pad). It builds its own canvas at runtime — no prefab, no scene setup, no
/// EventSystem needed, since keyboard, pad and mouse are all polled directly
/// through the Input System.
///
/// While open: Time.timeScale is 0, and the last rendered frame is captured
/// and blurred as a CRT backdrop.
/// </summary>
[AddComponentMenu("UI/Retro Pause/Pause Menu")]
[DisallowMultipleComponent]
public class PauseMenu : MonoBehaviour
{
    public enum FrameFlip { Auto, Never, Always }

    private enum State { Closed, Opening, Open, Closing }

    [Header("Look")]
    [SerializeField] private PauseMenuTheme theme = new PauseMenuTheme();

    [Tooltip("Canvas sorting order. Raise it if some other overlay draws on top of the menu.")]
    [SerializeField] private int sortingOrder = 5000;

    [Header("Entries")]
    [SerializeField]
    private PauseMenuEntryConfig[] entries =
    {
        new PauseMenuEntryConfig { action = PauseMenuAction.Resume,   label = "RESUME",   caption = "BACK TO THE TRACK" },
        new PauseMenuEntryConfig { action = PauseMenuAction.Restart,  label = "RESTART",  caption = "RESET TO GRID" },
        new PauseMenuEntryConfig { action = PauseMenuAction.Controls, label = "CONTROLS", caption = "INPUT MAP" },
        new PauseMenuEntryConfig { action = PauseMenuAction.Quit,     label = "QUIT",     caption = "END SESSION", danger = true }
    };

    [Header("Controls Page")]
    [SerializeField]
    private PauseMenuBinding[] bindings =
    {
        new PauseMenuBinding("THROTTLE",  "W / UP",      "RT"),
        new PauseMenuBinding("BRAKE",     "S / DOWN",    "LT"),
        new PauseMenuBinding("STEER",     "A D / L R",   "LEFT STICK"),
        new PauseMenuBinding("SHIFT UP",  "SHIFT",       "B"),
        new PauseMenuBinding("SHIFT DOWN","CTRL",        "X"),
        new PauseMenuBinding("HANDBRAKE", "SPACE",       "A"),
        new PauseMenuBinding("LOOK",      "—",           "RIGHT STICK"),
        new PauseMenuBinding("RESET CAR", "R",           ""),
        new PauseMenuBinding("PAUSE",     "ESC",         "START")
    };

    [Header("Input")]
    [Tooltip("Optional. If you add a Pause action to your .inputactions asset, hook it up here. Leave empty to use Esc / Start.")]
    [SerializeField] private InputActionReference pauseAction;

    [Tooltip("Let the mouse hover and click the menu entries.")]
    [SerializeField] private bool mouseSupport = true;

    [Tooltip("Free and show the cursor while paused, then put it back the way it was.")]
    [SerializeField] private bool releaseCursor = true;

    [Header("Freeze Frame")]
    [Tooltip("Capture the last rendered frame and use a blurred copy as the menu backdrop. Turn off for a plain dark wash.")]
    [SerializeField] private bool freezeFrameBackdrop = true;

    [Tooltip("How many halving steps the captured frame goes through. More = blurrier and cheaper.")]
    [Range(1, 6)] [SerializeField] private int blurSteps = 4;

    [Tooltip("Graphics APIs disagree about which way up a screen capture is. Auto follows the running API; flip it by hand if the backdrop is upside down.")]
    [SerializeField] private FrameFlip frameFlip = FrameFlip.Auto;

    [Header("Audio")]
    [Tooltip("Pause every AudioSource in the game while the menu is up. The menu's own blips keep playing.")]
    [SerializeField] private bool pauseGameAudio = true;

    [SerializeField] private bool uiSounds = true;
    [Range(0f, 1f)] [SerializeField] private float uiVolume = 0.6f;

    [Header("Quit")]
    [Tooltip("Scene to load on QUIT. Leave empty to quit the application (stops play mode in the editor).")]
    [SerializeField] private string quitSceneName = "";

    [Header("Events")]
    public UnityEvent onPaused;
    public UnityEvent onResumed;

    /// <summary>True while the menu is up. Cheap global for gameplay scripts to check.</summary>
    public static bool IsPaused { get; private set; }

    public static PauseMenu Instance { get; private set; }

    private PauseMenuView view;
    private State state = State.Closed;
    private float stateTime;
    private int selected;

    private float previousTimeScale = 1f;
    private CursorLockMode previousLockState;
    private bool previousCursorVisible;

    private RenderTexture frozenFrame;
    private Coroutine captureRoutine;

    private AudioSource audioSource;
    private AudioClip clipMove, clipSelect, clipBack, clipOpen, clipClose;

    private float navHoldTime;
    private int navHeldDirection;
    private int hoveredRow = -1;

    private const float NavRepeatDelay = 0.42f;
    private const float NavRepeatRate = 0.10f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[PauseMenu] A second PauseMenu was found — disabling this one.", this);
            enabled = false;
            return;
        }
        Instance = this;

        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;
        audioSource.bypassListenerEffects = true;
        // Without this the blips would go silent the moment we pause the game.
        audioSource.ignoreListenerPause = true;

        if (uiSounds)
        {
            clipMove = PauseMenuBleeps.Move();
            clipSelect = PauseMenuBleeps.Select();
            clipBack = PauseMenuBleeps.Back();
            clipOpen = PauseMenuBleeps.Open();
            clipClose = PauseMenuBleeps.Close();
        }
    }

    private void OnEnable()
    {
        if (pauseAction != null && pauseAction.action != null) pauseAction.action.Enable();
    }

    private void OnDisable()
    {
        if (pauseAction != null && pauseAction.action != null) pauseAction.action.Disable();

        // Being switched off mid-pause would otherwise leave the game frozen
        // behind a menu that no longer updates.
        if (IsPaused) RestoreGameState();
        if (state != State.Closed)
        {
            state = State.Closed;
            if (view != null) view.SetVisible(false);
            ReleaseFrozenFrame();
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (IsPaused) RestoreGameState();

        if (view != null) view.Destroy();
        ReleaseFrozenFrame();
    }

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        if (TogglePressed())
        {
            if (state == State.Closed || state == State.Closing) Pause();
            else if (view != null && view.CurrentPage != PauseMenuView.Page.Root) ShowRootPage();
            else Resume();
        }

        switch (state)
        {
            case State.Opening:
                stateTime += dt;
                float openT = theme.openDuration > 0f ? Mathf.Clamp01(stateTime / theme.openDuration) : 1f;
                view.SetOpenProgress(openT);
                if (openT >= 1f) state = State.Open;
                HandleMenuInput(dt);
                break;

            case State.Open:
                HandleMenuInput(dt);
                break;

            case State.Closing:
                stateTime += dt;
                float closeT = theme.closeDuration > 0f ? Mathf.Clamp01(stateTime / theme.closeDuration) : 1f;
                view.SetOpenProgress(1f - closeT);
                if (closeT >= 1f)
                {
                    state = State.Closed;
                    view.SetVisible(false);
                    ReleaseFrozenFrame();
                }
                break;
        }

        if (state != State.Closed && view != null)
        {
            view.Tick(dt, Time.unscaledTime, Time.timeSinceLevelLoad);
        }
    }

    // ------------------------------------------------------------------ state

    public void Toggle()
    {
        if (state == State.Closed || state == State.Closing) Pause();
        else Resume();
    }

    public void Pause()
    {
        if (state == State.Opening || state == State.Open) return;

        EnsureView();

        previousTimeScale = Time.timeScale > 0.0001f ? Time.timeScale : 1f;
        Time.timeScale = 0f;
        IsPaused = true;

        if (pauseGameAudio) AudioListener.pause = true;

        if (releaseCursor)
        {
            previousLockState = Cursor.lockState;
            previousCursorVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        selected = 0;
        hoveredRow = -1;
        navHeldDirection = 0;
        state = State.Opening;
        stateTime = 0f;

        view.SetVisible(true);
        view.SetPage(PauseMenuView.Page.Root, false);
        view.SetSelection(selected);
        view.SetOpenProgress(0f);
        view.SetFrozenFrame(null, false);

        view.SetTelemetry(false, 0f, 0f, 0f, 0f, 0f, false, "-");
        Play(clipOpen);

        if (freezeFrameBackdrop)
        {
            if (captureRoutine != null) StopCoroutine(captureRoutine);
            captureRoutine = StartCoroutine(CaptureFrozenFrame());
        }

        onPaused.Invoke();
    }

    public void Resume()
    {
        if (state == State.Closed || state == State.Closing) return;

        state = State.Closing;
        stateTime = 0f;

        RestoreGameState();
        Play(clipClose);
        onResumed.Invoke();
    }

    private void RestoreGameState()
    {
        Time.timeScale = previousTimeScale;
        IsPaused = false;

        if (pauseGameAudio) AudioListener.pause = false;

        if (releaseCursor)
        {
            Cursor.lockState = previousLockState;
            Cursor.visible = previousCursorVisible;
        }
    }

    private void EnsureView()
    {
        if (view != null && view.IsBuilt) return;

        List<PauseMenuEntryInfo> infos = new List<PauseMenuEntryInfo>(entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            infos.Add(new PauseMenuEntryInfo(entries[i].label, entries[i].caption, entries[i].danger));
        }

        view = new PauseMenuView(theme);
        // Built as a root object on purpose: a Screen Space Overlay canvas
        // parented under a moved or scaled transform lays itself out wrong.
        view.Build(null, infos, bindings, SceneManager.GetActiveScene().name, sortingOrder);
        view.SetVisible(false);
    }

    // ------------------------------------------------------------------ input

    private bool TogglePressed()
    {
        if (pauseAction != null && pauseAction.action != null)
        {
            if (pauseAction.action.WasPressedThisFrame()) return true;
        }

        Keyboard kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) return true;

        Gamepad pad = Gamepad.current;
        if (pad != null && pad.startButton.wasPressedThisFrame) return true;

        return false;
    }

    private void HandleMenuInput(float dt)
    {
        if (view == null) return;

        if (view.CurrentPage != PauseMenuView.Page.Root)
        {
            if (CancelPressed()) ShowRootPage();
            return;
        }

        if (entries.Length == 0) return;

        int direction = ReadNavDirection();
        if (direction != 0)
        {
            if (direction != navHeldDirection)
            {
                navHeldDirection = direction;
                navHoldTime = 0f;
                Move(direction);
            }
            else
            {
                navHoldTime += dt;
                if (navHoldTime >= NavRepeatDelay)
                {
                    navHoldTime -= NavRepeatRate;
                    Move(direction);
                }
            }
        }
        else
        {
            navHeldDirection = 0;
            navHoldTime = 0f;
        }

        if (mouseSupport) HandleMouse();

        if (SubmitPressed()) Confirm(entries[selected].action);
    }

    private int ReadNavDirection()
    {
        int direction = 0;

        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) direction -= 1;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) direction += 1;
        }

        Gamepad pad = Gamepad.current;
        if (pad != null)
        {
            if (pad.dpad.up.isPressed) direction -= 1;
            if (pad.dpad.down.isPressed) direction += 1;

            float stickY = pad.leftStick.ReadValue().y;
            if (stickY > 0.55f) direction -= 1;
            else if (stickY < -0.55f) direction += 1;
        }

        return Mathf.Clamp(direction, -1, 1);
    }

    private bool SubmitPressed()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame))
            return true;

        Gamepad pad = Gamepad.current;
        if (pad != null && pad.buttonSouth.wasPressedThisFrame) return true;

        if (mouseSupport)
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && hoveredRow >= 0)
            {
                selected = hoveredRow;
                view.SetSelection(selected);
                return true;
            }
        }

        return false;
    }

    private bool CancelPressed()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) return true;

        Gamepad pad = Gamepad.current;
        if (pad != null && pad.buttonEast.wasPressedThisFrame) return true;

        if (mouseSupport)
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.wasPressedThisFrame) return true;
        }

        return false;
    }

    private void HandleMouse()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        int row = view.HitTest(mouse.position.ReadValue());
        if (row == hoveredRow) return;

        hoveredRow = row;
        if (row >= 0 && row != selected)
        {
            selected = row;
            view.SetSelection(selected);
            Play(clipMove);
        }
    }

    private void Move(int direction)
    {
        if (entries.Length == 0) return;

        selected = (selected + direction + entries.Length) % entries.Length;
        view.SetSelection(selected);
        Play(clipMove);
    }

    private void ShowRootPage()
    {
        view.SetPage(PauseMenuView.Page.Root, true);
        Play(clipBack);
    }

    private void Confirm(PauseMenuAction action)
    {
        Play(clipSelect);

        switch (action)
        {
            case PauseMenuAction.Resume:
                Resume();
                break;

            case PauseMenuAction.Restart:
                RestoreGameState();
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
                break;

            case PauseMenuAction.Controls:
                view.SetPage(PauseMenuView.Page.Controls, true);
                break;

            case PauseMenuAction.Quit:
                QuitGame();
                break;
        }
    }

    private void QuitGame()
    {
        RestoreGameState();

        if (!string.IsNullOrEmpty(quitSceneName))
        {
            SceneManager.LoadScene(quitSceneName);
            return;
        }

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ------------------------------------------------------------ freeze frame

    private IEnumerator CaptureFrozenFrame()
    {
        // Wait for the game to finish drawing this frame — the menu itself is
        // still hidden at this point, so it can't end up in its own backdrop.
        yield return new WaitForEndOfFrame();

        int width = Screen.width;
        int height = Screen.height;
        if (width < 8 || height < 8) yield break;

        RenderTexture full = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.Default);
        full.filterMode = FilterMode.Bilinear;
        ScreenCapture.CaptureScreenshotIntoRenderTexture(full);

        // Repeated bilinear halving is a perfectly good box blur and needs no
        // shader of its own.
        RenderTexture current = full;
        for (int i = 0; i < blurSteps; i++)
        {
            int w = Mathf.Max(8, current.width / 2);
            int h = Mathf.Max(8, current.height / 2);

            RenderTexture next = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.Default);
            next.filterMode = FilterMode.Bilinear;
            Graphics.Blit(current, next);

            RenderTexture.ReleaseTemporary(current);
            current = next;
        }

        ReleaseFrozenFrame();
        frozenFrame = new RenderTexture(current.width, current.height, 0, RenderTextureFormat.Default)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "PauseMenu_FrozenFrame"
        };
        Graphics.Blit(current, frozenFrame);
        RenderTexture.ReleaseTemporary(current);

        bool flip;
        switch (frameFlip)
        {
            case FrameFlip.Always: flip = true; break;
            case FrameFlip.Never: flip = false; break;
            default: flip = SystemInfo.graphicsUVStartsAtTop; break;
        }

        view.SetFrozenFrame(frozenFrame, flip);
        captureRoutine = null;
    }

    private void ReleaseFrozenFrame()
    {
        if (frozenFrame == null) return;

        if (view != null) view.SetFrozenFrame(null, false);
        frozenFrame.Release();
        Destroy(frozenFrame);
        frozenFrame = null;
    }

    // ------------------------------------------------------------------ audio

    private void Play(AudioClip clip)
    {
        if (!uiSounds || clip == null || audioSource == null) return;
        audioSource.PlayOneShot(clip, uiVolume);
    }
}
