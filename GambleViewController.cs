using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ZipSaber
{
    /// <summary>
    /// Note Slots: a three-reel slot machine made of Beat Saber notes.
    ///   3 dots                         → Beat Saber closes
    ///   3 bombs                        → the menu glitches out for 30 seconds (gentle, no flashing)
    ///   3 identical notes, same angle  → 5 seconds of fireworks
    /// </summary>
    [ViewDefinition("ZipSaber.gamble.bsml")]
    internal class GambleViewController : BSMLAutomaticViewController
    {
        // ── Symbols ───────────────────────────────────────────────────────────────
        internal enum Sym { RedArrow, BlueArrow, RedDot, BlueDot, Bomb }

        internal struct Note
        {
            internal Sym Sym;
            internal int Dir;   // 0 down, 1 up, 2 left, 3 right, 4 down-left, 5 down-right, 6 up-left, 7 up-right (arrows only)
            internal bool SameAs(Note o) => Sym == o.Sym && (!IsArrow(Sym) || Dir == o.Dir);
        }

        private static readonly (Sym Sym, float Weight)[] SymWeights =
        {
            (Sym.RedArrow, 3f), (Sym.BlueArrow, 3f), (Sym.RedDot, 1.5f), (Sym.BlueDot, 1.5f), (Sym.Bomb, 2f),
        };

        // Downward cuts are by far the most common in real maps, so they come up most here too
        private static readonly float[] DirWeights = { 0.50f, 0.14f, 0.08f, 0.08f, 0.05f, 0.05f, 0.05f, 0.05f };
        private static readonly float[] DirAngles  = { 0f, 180f, -90f, 90f, -45f, 45f, -135f, 135f };

        private static float TotalWeight => SymWeights.Sum(w => w.Weight);
        private static float P(Sym s) => SymWeights.First(w => w.Sym == s).Weight / TotalWeight;
        private static float DirTotal => DirWeights.Sum();
        private static float SameDirChance => DirWeights.Sum(w => Mathf.Pow(w / DirTotal, 3));

        // Exact odds of each headline result
        private static float OddsDots      => Mathf.Pow(P(Sym.RedDot) + P(Sym.BlueDot), 3);
        private static float OddsBombs     => Mathf.Pow(P(Sym.Bomb), 3);
        private static float OddsSameArrow => Mathf.Pow(P(Sym.RedArrow), 3) + Mathf.Pow(P(Sym.BlueArrow), 3);
        private static float OddsFireworks => OddsSameArrow * SameDirChance;
        private static float OddsFullCombo => OddsSameArrow * (1f - SameDirChance);
        private static string OneIn(float p) => p <= 0f ? "never" : $"1 in {Mathf.RoundToInt(1f / p)}";

        private static readonly Color Red     = new Color(0.86f, 0.17f, 0.20f);
        private static readonly Color Blue    = new Color(0.17f, 0.42f, 0.92f);
        private static readonly Color BombCol = new Color(0.16f, 0.16f, 0.19f);

        internal static bool IsDot(Sym s)   => s == Sym.RedDot || s == Sym.BlueDot;
        internal static bool IsArrow(Sym s) => s == Sym.RedArrow || s == Sym.BlueArrow;

        private static Note Roll()
        {
            float r = UnityEngine.Random.value * TotalWeight;
            Sym sym = Sym.RedArrow;
            foreach (var (s, w) in SymWeights) { if (r < w) { sym = s; break; } r -= w; }
            int dir = 0;
            if (IsArrow(sym))
            {
                float d = UnityEngine.Random.value * DirTotal;
                for (int i = 0; i < DirWeights.Length; i++) { if (d < DirWeights[i]) { dir = i; break; } d -= DirWeights[i]; }
            }
            return new Note { Sym = sym, Dir = dir };
        }

        // ── Session state ─────────────────────────────────────────────────────────
        private static bool _armed;
        private static int _spins, _nearMisses, _fullCombos, _fireworks, _glitches;
        private static bool _jackpotTriggered;

        // ── UI refs ───────────────────────────────────────────────────────────────
        [UIComponent("zs-tag")]    private TextMeshProUGUI _tag = null;
        [UIComponent("how-title")] private TextMeshProUGUI _howTitle = null;
        [UIObject("machine")]      private GameObject _machine = null;
        [UIObject("help-slot")]    private GameObject _helpSlot = null;
        [UIObject("test-row")]     private GameObject _testRow = null;
        [UIComponent("test-title")] private TextMeshProUGUI _testTitle = null;

        private Reel[] _reels;
        private TextMeshProUGUI _title, _result, _odds;
        private Image _spinBtn, _payline;
        private TextMeshProUGUI _spinLabel;
        private SlotAudio _audio;
        private bool _spinning;
        private GameObject _helpPopup;

        [UIValue("stats-label")]
        public string StatsLabel => $"{_spins} spins  <color=#7ED957>{_fullCombos} FC</color>  <color=#F2C94C>{_fireworks} fw</color>";

        [UIValue("rules-text")]
        public string RulesText =>
            "Pull the lever and three reels spin up Beat Saber notes: red or blue <b>arrows</b> (any cut direction), " +
            "red or blue <b>dots</b>, and <b>bombs</b>.\n\n" +
            "Line up three of something and something happens.\n" +
            "<color=#FF5E6B><b>Three dots closes Beat Saber.</b></color>\n" +
            "Hover the <b>?</b> up top for every result and its odds.";

        [UIValue("armed")]
        public bool Armed { get => _armed; set => _armed = value; }

        [UIAction("armed-changed")]
        private void OnArmedChanged(bool value) { _armed = value; RefreshSpinButton(); }

        [UIAction("go-back")]
        private void OnGoBack() => ModManagerFlowCoordinator.GoBack();

        // ── Lifecycle ─────────────────────────────────────────────────────────────
        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            if (firstActivation) FlatView.Prepare(this);
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            if (firstActivation)
            {
                FlatView.Finish(this);
                _audio = SlotAudio.Create(transform);
                BuildMachine();
                BuildHelp();
                BuildTestButtons();
            }
            Theme.Changed -= ApplyTheme;
            Theme.Changed += ApplyTheme;
            ApplyTheme();
            RefreshSpinButton();
            NotifyPropertyChanged(nameof(StatsLabel));
        }

        protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            Theme.Changed -= ApplyTheme;
            if (_helpPopup != null) _helpPopup.SetActive(false);
            base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
        }

        private void ApplyTheme()
        {
            var a = Theme.Accent;
            if (_tag != null) _tag.color = a;
            if (_howTitle != null) _howTitle.color = a;
            if (_testTitle != null) _testTitle.color = a;
            if (_title != null) _title.color = a;
            if (_payline != null) _payline.color = new Color(a.r, a.g, a.b, 0.55f);
            RefreshSpinButton();
        }

        // ── Build: machine ─────────────────────────────────────────────────────────
        private void BuildMachine()
        {
            if (_machine == null) return;
            var root = _machine.transform;

            _title = UiKit.Text(root, "Title", "NOTE  SLOTS", 4.2f, Theme.Accent);
            _title.fontStyle = FontStyles.Bold | FontStyles.Italic; _title.characterSpacing = 6;
            UiKit.Size(_title.gameObject, 56, 6);

            var housing = UiKit.Img(root, "Housing", UiKit.RoundSprite, new Color(0.03f, 0.03f, 0.05f, 0.95f), sliced: true);
            UiKit.Size(housing.gameObject, 56, 22);
            var row = housing.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.spacing = 2; row.padding = new RectOffset(3, 3, 2, 2);
            row.childAlignment = TextAnchor.MiddleCenter;
            row.childControlWidth = true; row.childControlHeight = true;
            row.childForceExpandWidth = false; row.childForceExpandHeight = false;

            _reels = new Reel[3];
            for (int i = 0; i < 3; i++) _reels[i] = new Reel(housing.transform, i);

            _payline = UiKit.Img(housing.transform, "Payline", UiKit.RoundSprite, new Color(1, 1, 1, 0.5f), sliced: true);
            _payline.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            var prt = _payline.rectTransform;
            prt.anchorMin = new Vector2(0, 0.5f); prt.anchorMax = new Vector2(1, 0.5f);
            prt.sizeDelta = new Vector2(-2, 0.35f); prt.anchoredPosition = Vector2.zero;
            _payline.transform.SetAsLastSibling();

            _result = UiKit.Text(root, "Result", "Feeling lucky?", 3.4f, new Color(0.85f, 0.85f, 0.9f));
            _result.fontStyle = FontStyles.Bold;
            UiKit.Size(_result.gameObject, 56, 6);

            _spinBtn = UiKit.Button(root, "Spin", Theme.Accent, 40, 9, OnSpin, "PULL THE LEVER", 3.4f);
            _spinLabel = _spinBtn.GetComponentInChildren<TextMeshProUGUI>();

            _odds = UiKit.Text(root, "Odds", $"<color=#FF5E6B>3 dots = game closes</color>  <color=#77778A>({OneIn(OddsDots)})</color>", 2.4f, Color.white);
            UiKit.Size(_odds.gameObject, 56, 4);

            foreach (var r in _reels) r.ShowStatic(Roll());
        }

        // ── Build: "?" hover help ──────────────────────────────────────────────────
        private void BuildHelp()
        {
            if (_helpSlot == null) return;

            // The "?" button in the header
            var q = UiKit.Button(_helpSlot.transform, "HelpBtn", new Color(0.16f, 0.16f, 0.21f, 0.95f), 7, 7, null, "?", 4.2f);
            var et = q.GetComponent<EventTrigger>() ?? q.gameObject.AddComponent<EventTrigger>();
            UiKit.AddTrigger(et, EventTriggerType.PointerEnter, () => ShowHelp(true));
            UiKit.AddTrigger(et, EventTriggerType.PointerExit,  () => ShowHelp(false));

            // The popup: floats over the view, never takes the pointer (so it can't flicker)
            var popup = UiKit.Img(transform, "HelpPopup", UiKit.RoundSprite, new Color(0.05f, 0.05f, 0.08f, 0.97f), sliced: true);
            _helpPopup = popup.gameObject;
            var rt = popup.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(96f, 62f);
            rt.anchoredPosition = new Vector2(0f, -4f);
            rt.localPosition += new Vector3(0, 0, -1.5f); // a touch in front of the panels

            var border = UiKit.Img(rt, "Border", UiKit.RoundSprite, Theme.AccentShade(1f, 0.6f), sliced: true);
            UiKit.Stretch(border.rectTransform, -0.4f);
            border.transform.SetAsFirstSibling();

            var col = UiKit.Rect("Rows", rt);
            UiKit.Stretch(col, 3f);
            var v = col.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = 1.2f; v.childAlignment = TextAnchor.UpperLeft;
            v.childControlWidth = true; v.childControlHeight = true;
            v.childForceExpandWidth = true; v.childForceExpandHeight = false;

            var title = UiKit.Text(col, "Title", "WHAT THE REELS DO", 3.2f, Theme.Accent, TextAlignmentOptions.Left);
            title.fontStyle = FontStyles.Bold; title.characterSpacing = 4;
            UiKit.Size(title.gameObject, -1, 5);

            HelpRow(col, new[] { N(Sym.RedDot), N(Sym.BlueDot), N(Sym.RedDot) },
                "<color=#FF5E6B><b>3 dots</b></color>  Crashes your game. Beat Saber closes after a 5 second countdown.", OddsDots);
            HelpRow(col, new[] { N(Sym.Bomb), N(Sym.Bomb), N(Sym.Bomb) },
                "<color=#C58BFF><b>3 bombs</b></color>  The menu glitches out for 30 seconds. Gentle wobble, no flashing.", OddsBombs);
            HelpRow(col, new[] { N(Sym.BlueArrow, 0), N(Sym.BlueArrow, 0), N(Sym.BlueArrow, 0) },
                "<color=#F2C94C><b>Same note, same direction</b></color>  Fireworks for 5 seconds.", OddsFireworks);
            HelpRow(col, new[] { N(Sym.RedArrow, 0), N(Sym.RedArrow, 3), N(Sym.RedArrow, 5) },
                "<color=#7ED957><b>3 arrows, same colour</b></color>  Full Combo!", OddsFullCombo);
            HelpRow(col, new[] { N(Sym.RedDot), N(Sym.BlueDot), N(Sym.BlueArrow, 1) },
                "<color=#9A9AA6><b>2 dots</b></color>  So close... (nothing happens)", -1f);

            _helpPopup.SetActive(false);
        }

        private static Note N(Sym s, int dir = 0) => new Note { Sym = s, Dir = dir };

        private static void HelpRow(Transform parent, Note[] notes, string text, float odds)
        {
            var row = UiKit.Rect("Row", parent);
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 1f; h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = true; h.childControlHeight = true;
            h.childForceExpandWidth = false; h.childForceExpandHeight = false;
            UiKit.Size(row.gameObject, -1, 8);

            foreach (var n in notes)
            {
                var holder = UiKit.Rect("Mini", row);
                UiKit.Size(holder.gameObject, 6, 6);
                var sv = new SymbolView(holder, "m", 5.6f);
                sv.Set(n);
            }

            var spacer = UiKit.Rect("Gap", row); UiKit.Size(spacer.gameObject, 1.5f, 6);

            string oddsText = odds >= 0f ? $"  <color=#6E6E80>({OneIn(odds)})</color>" : "";
            var t = UiKit.Text(row, "Text", text + oddsText, 2.5f, new Color(0.85f, 0.85f, 0.9f), TextAlignmentOptions.Left);
            t.enableWordWrapping = true;
            var le = UiKit.Size(t.gameObject, 64, 8);
            le.flexibleWidth = 1;
        }

        private void ShowHelp(bool on)
        {
            if (_helpPopup == null) return;
            _helpPopup.SetActive(on);
            if (on) _helpPopup.transform.SetAsLastSibling();
        }

        private void RefreshSpinButton()
        {
            if (_spinBtn == null) return;
            bool can = _armed && !_spinning && !_jackpotTriggered;
            UiKit.Recolor(_spinBtn, can ? Theme.Accent : new Color(0.22f, 0.22f, 0.26f, 1f));
            if (_spinLabel != null)
                _spinLabel.text = _jackpotTriggered ? (_testSpin ? "TEST JACKPOT" : "GOODBYE") : _spinning ? (_testSpin ? "TEST SPIN..." : "SPINNING...") : _armed ? "PULL THE LEVER" : "ACCEPT THE RISK FIRST";
            var b = _spinBtn.GetComponent<Button>();
            if (b != null) b.interactable = can;
        }

        // ── Spin ──────────────────────────────────────────────────────────────────
        private void OnSpin()
        {
            if (!_armed || _spinning || _jackpotTriggered || _reels == null) return;
            StartCoroutine(SpinRoutine(null));
        }

        // ── Test buttons: force a result (never closes the game) ─────────────────
        private bool _testSpin;

        private void BuildTestButtons()
        {
            if (_testRow == null) return;
            var row = _testRow.transform;
            var c = new Color(0.18f, 0.18f, 0.24f, 0.95f);
            UiKit.Button(row, "TDots",  c, 10, 6, () => ForceSpin(new[] { N(Sym.RedDot), N(Sym.BlueDot), N(Sym.RedDot) }), "DOTS", 2.2f);
            UiKit.Button(row, "TBombs", c, 10, 6, () => ForceSpin(new[] { N(Sym.Bomb), N(Sym.Bomb), N(Sym.Bomb) }), "BOMBS", 2.2f);
            UiKit.Button(row, "TFw",    c, 11, 6, () => { int d = UnityEngine.Random.Range(0, 8); var s = UnityEngine.Random.value < 0.5f ? Sym.RedArrow : Sym.BlueArrow; ForceSpin(new[] { N(s, d), N(s, d), N(s, d) }); }, "FIREWK", 2.2f);
            UiKit.Button(row, "TFc",    c, 8, 6,  () => ForceSpin(new[] { N(Sym.BlueArrow, 0), N(Sym.BlueArrow, 2), N(Sym.BlueArrow, 5) }), "FC", 2.2f);
        }

        private void ForceSpin(Note[] outcome)
        {
            if (_spinning || _jackpotTriggered || _reels == null) return;
            Plugin.Log?.Info("[Slots] Test spin: " + string.Join(", ", outcome.Select(o => o.Sym + (IsArrow(o.Sym) ? "/" + o.Dir : ""))));
            StartCoroutine(SpinRoutine(outcome));
        }

        private IEnumerator SpinRoutine(Note[] forced)
        {
            _testSpin = forced != null;
            _spinning = true;
            _spins++;
            NotifyPropertyChanged(nameof(StatsLabel));
            RefreshSpinButton();
            _result.text = "<color=#9A9AA6>...</color>";
            _audio.Lever();

            var outcome = forced ?? new[] { Roll(), Roll(), Roll() };
            for (int i = 0; i < 3; i++) _reels[i].StartSpin(_audio);

            // Hold the last reel longer when the first two already match (tension!)
            bool tension = outcome[0].SameAs(outcome[1]) || (IsDot(outcome[0].Sym) && IsDot(outcome[1].Sym))
                           || (outcome[0].Sym == Sym.Bomb && outcome[1].Sym == Sym.Bomb);
            float[] stopAt = { 1.0f, 1.55f, tension ? 3.2f : 2.1f };

            float t0 = Time.time;
            for (int i = 0; i < 3; i++)
            {
                while (Time.time - t0 < stopAt[i]) yield return null;
                if (i == 2 && tension) _result.text = "<color=#F2C94C><b>TWO OF A KIND...</b></color>";
                _reels[i].RequestStop(outcome[i]);
                while (!_reels[i].Stopped) yield return null;
                _audio.ReelStop(i);
            }

            yield return new WaitForSeconds(0.15f);
            Resolve(outcome);
            if (!_jackpotTriggered) _testSpin = false;
            _spinning = false;
            RefreshSpinButton();
            NotifyPropertyChanged(nameof(StatsLabel));
        }

        private void Resolve(Note[] o)
        {
            int dots = o.Count(n => IsDot(n.Sym));

            if (dots == 3) { Jackpot(); return; }

            if (o.All(n => n.Sym == Sym.Bomb))
            {
                _glitches++;
                _result.text = "<color=#C58BFF><b>TRIPLE BOMB.</b> Something's wrong with the menu...</color>";
                _audio.BombHit();
                GlitchEffect.Run(this, 30f);
                return;
            }
            if (o[0].SameAs(o[1]) && o[1].SameAs(o[2]))
            {
                _fireworks++;
                _result.text = "<color=#F2C94C><b>PERFECT LINE!</b> Fireworks!</color>";
                _audio.SmallWin();
                foreach (var r in _reels) r.Celebrate();
                Fireworks.Run(this, transform, 5f, _audio);
                return;
            }
            if (o.All(n => n.Sym == Sym.RedArrow) || o.All(n => n.Sym == Sym.BlueArrow))
            {
                _fullCombos++;
                _result.text = "<color=#7ED957><b>FULL COMBO!</b></color>";
                _audio.SmallWin();
                return;
            }
            if (o.Any(n => n.Sym == Sym.Bomb))
            {
                _result.text = "<color=#9A9AA6><b>BOMB!</b> Combo broken.</color>";
                _audio.BombHit();
                return;
            }
            if (dots == 2)
            {
                _nearMisses++;
                _result.text = "<color=#F2C94C><b>Two dots...</b> so close.</color>";
                _audio.NearMiss();
                return;
            }
            _result.text = o.All(n => IsArrow(n.Sym)) ? "<color=#C8C8D2>All arrows. Swing on.</color>" : "<color=#9A9AA6>Miss.</color>";
            _audio.Lose();
        }

        // ── Jackpot: the game closes ───────────────────────────────────────────────
        private void Jackpot()
        {
            _jackpotTriggered = true;
            RefreshSpinButton();
            Plugin.Log?.Warn(_testSpin ? "[Slots] Test jackpot - countdown only, not closing." : "[Slots] JACKPOT - three dots. Closing Beat Saber as promised.");
            _audio.Jackpot();
            foreach (var r in _reels) r.Celebrate();

            // Runs on its own object so backing out of the menu doesn't save you.
            var go = new GameObject("ZipSaber_Jackpot");
            DontDestroyOnLoad(go);
            go.AddComponent<JackpotCloser>().Begin(this, _testSpin);
        }

        internal void ShowCountdown(int n)
        {
            if (this == null || _result == null) return;
            _result.text = n > 0
                ? $"<color=#FF5E6B><b>JACKPOT!</b></color>  Closing in <b>{n}</b>"
                : "<color=#FF5E6B><b>GOODBYE</b></color>";
            _audio?.Countdown(n);
            if (_title != null) _title.color = (n % 2 == 0) ? Theme.Accent : new Color(1f, 0.37f, 0.42f);
        }

        internal void FinalBoom() { if (this != null) _audio?.FinalBoom(); }

        internal void EndTestJackpot()
        {
            if (this == null) return;
            _jackpotTriggered = false;
            _testSpin = false;
            if (_result != null) _result.text = "<color=#7ED957><b>TEST</b></color> - this is where Beat Saber would close.";
            if (_title != null) _title.color = Theme.Accent;
            RefreshSpinButton();
        }

        private class JackpotCloser : MonoBehaviour
        {
            private GambleViewController _vc;
            private bool _test;
            internal void Begin(GambleViewController vc, bool test) { _vc = vc; _test = test; StartCoroutine(Run()); }

            private IEnumerator Run()
            {
                yield return new WaitForSecondsRealtime(1.4f);
                for (int n = 5; n >= 1; n--)
                {
                    if (_vc != null) _vc.ShowCountdown(n);
                    yield return new WaitForSecondsRealtime(1f);
                }
                if (_vc != null) { _vc.ShowCountdown(0); _vc.FinalBoom(); }
                yield return new WaitForSecondsRealtime(0.9f);
                if (_test)
                {
                    if (_vc != null) _vc.EndTestJackpot();
                    Destroy(gameObject);
                    yield break;
                }
                Plugin.Log?.Warn("[Slots] Application.Quit()");
                Application.Quit();
            }
        }

        // ── Reel ──────────────────────────────────────────────────────────────────
        private class Reel
        {
            private const float SymbolSize = 15f;
            private const float SpinSpeed  = 14f;   // symbols per second

            private readonly RectTransform _strip;
            private readonly SymbolView _a, _b;      // _a = in the window, _b = coming in from above
            private readonly Image _window;

            private float _offset, _speed, _landT;
            private Note? _target;
            private bool _landing;
            private SlotAudio _audio;

            internal bool Stopped { get; private set; } = true;

            internal Reel(Transform parent, int index)
            {
                _window = UiKit.Img(parent, $"Reel{index}", UiKit.RoundSprite, new Color(0.11f, 0.11f, 0.15f, 1f), sliced: true);
                UiKit.Size(_window.gameObject, 15, 18);
                _window.gameObject.AddComponent<RectMask2D>();

                _strip = UiKit.Rect("Strip", _window.transform);
                _strip.anchorMin = _strip.anchorMax = new Vector2(0.5f, 0.5f);
                _strip.sizeDelta = new Vector2(SymbolSize, SymbolSize);

                _a = new SymbolView(_strip, "A", 13f);
                _b = new SymbolView(_strip, "B", 13f);
                _window.gameObject.AddComponent<ReelDriver>().Reel = this;
                Layout();
            }

            internal void ShowStatic(Note n) { _a.Set(n); _b.Set(Roll()); _offset = 0f; Layout(); Stopped = true; }

            internal void StartSpin(SlotAudio audio)
            {
                _audio = audio; _target = null; _landing = false; Stopped = false; _speed = 0f;
                _a.Glow(false); _b.Glow(false);
            }

            internal void RequestStop(Note target) => _target = target;
            internal void Celebrate() => _a.Glow(true);

            internal void Tick(float dt)
            {
                if (Stopped) return;

                if (_landing)
                {
                    _landT += dt / 0.38f;
                    float t = Mathf.Clamp01(_landT);
                    const float back = 1.70158f * 1.4f;
                    _offset = 1f + (back + 1f) * Mathf.Pow(t - 1f, 3) + back * Mathf.Pow(t - 1f, 2); // easeOutBack
                    if (t >= 1f) { _a.CopyFrom(_b); _offset = 0f; _b.Set(Roll()); _landing = false; Stopped = true; }
                    Layout();
                    return;
                }

                _speed = Mathf.MoveTowards(_speed, SpinSpeed, SpinSpeed * 4f * dt);
                _offset += _speed * dt;
                while (_offset >= 1f)
                {
                    _offset -= 1f;
                    _a.CopyFrom(_b);
                    if (_target.HasValue)
                    {
                        _b.Set(_target.Value);
                        _target = null;
                        _landing = true; _landT = 0f; _offset = 0f;
                        _audio?.Tick(0.3f);
                        break;
                    }
                    _b.Set(Roll());
                    _audio?.Tick(_speed / SpinSpeed);
                }
                Layout();
            }

            private void Layout()
            {
                _a.Rt.anchoredPosition = new Vector2(0, -_offset * SymbolSize * 1.15f);
                _b.Rt.anchoredPosition = new Vector2(0, (1f - _offset) * SymbolSize * 1.15f);
            }
        }

        private class ReelDriver : MonoBehaviour
        {
            internal Reel Reel;
            private void Update() => Reel?.Tick(Time.deltaTime);
        }

        // A note drawn from procedural sprites: body + arrow/dot, or a bomb
        internal class SymbolView
        {
            internal readonly RectTransform Rt;
            private readonly Image _body, _mark, _bomb, _glint, _halo;
            internal Note Current { get; private set; }

            internal SymbolView(Transform parent, string name, float size)
            {
                Rt = UiKit.Rect("Sym" + name, parent);
                Rt.anchorMin = Rt.anchorMax = new Vector2(0.5f, 0.5f);
                Rt.sizeDelta = new Vector2(size, size);

                _halo  = UiKit.Img(Rt, "Halo",  IconFactory.Circle,    new Color(1, 1, 1, 0f));        UiKit.Stretch(_halo.rectTransform, -size * 0.23f);
                _body  = UiKit.Img(Rt, "Body",  IconFactory.NoteBody,  Red);                           UiKit.Stretch(_body.rectTransform);
                _mark  = UiKit.Img(Rt, "Mark",  IconFactory.NoteArrow, Color.white);                   UiKit.Stretch(_mark.rectTransform);
                _bomb  = UiKit.Img(Rt, "Bomb",  IconFactory.Bomb,      BombCol);                       UiKit.Stretch(_bomb.rectTransform, -size * 0.04f);
                _glint = UiKit.Img(Rt, "Glint", IconFactory.BombGlint, new Color(1, 1, 1, 0.55f));     UiKit.Stretch(_glint.rectTransform, -size * 0.04f);
            }

            internal void Set(Note n)
            {
                Current = n;
                bool bomb = n.Sym == Sym.Bomb;
                _body.enabled = !bomb; _mark.enabled = !bomb;
                _bomb.enabled = bomb;  _glint.enabled = bomb;
                if (!bomb)
                {
                    _body.color = (n.Sym == Sym.RedArrow || n.Sym == Sym.RedDot) ? Red : Blue;
                    _mark.sprite = IsDot(n.Sym) ? IconFactory.NoteDot : IconFactory.NoteArrow;
                }
                Rt.localRotation = Quaternion.Euler(0, 0, IsArrow(n.Sym) ? DirAngles[n.Dir] : 0f);
            }

            internal void CopyFrom(SymbolView o) => Set(o.Current);

            internal void Glow(bool on) => _halo.color = on ? new Color(1f, 0.85f, 0.3f, 0.45f) : new Color(1, 1, 1, 0f);
        }
    }
}
