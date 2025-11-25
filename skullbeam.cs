using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using Duckov;   // AudioManager, CharacterMainControl, Health

namespace skullbeam
{
    // Duckov 모드 로더 엔트리
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        protected override void OnAfterSetup()
        {
            try
            {
                GameObject root = new GameObject("SkullBeamRoot");
                UnityEngine.Object.DontDestroyOnLoad(root);
                root.AddComponent<SkullBeamHUD>();

                Debug.Log("[skullbeam] OnAfterSetup - HUD 초기화 완료");
            }
            catch (Exception ex)
            {
                Debug.Log("[skullbeam] OnAfterSetup 예외: " + ex);
            }
        }
    }

    public class SkullBeamHUD : MonoBehaviour
    {
        // ───────────── 해골 위치 (랜덤) ─────────────
        private const float SKULL_WIDTH = 96f;
        private const float SKULL_HEIGHT = 96f;

        private float _skullPosX = -1f; // 해골 좌상단 X (GUI 좌표)
        private float _skullPosY = -1f; // 해골 좌상단 Y

        private static readonly BindingFlags BINDING_FLAGS =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // ───────────── 해골 텍스처 ─────────────
        private Texture2D _skullIdle;
        private Texture2D _skullCharge;
        private Texture2D _skullFire;
        private bool _texturesLoaded = false;

        // ───────────── 상태 ─────────────
        private enum BeamState
        {
            Off,
            Charge,
            Fire,
            Fade
        }

        private BeamState _state = BeamState.Off;
        private float _stateTimer = 0f;
        private float _beamLength = 0f;
        private float _beamAlpha = 1f;

        private const float ChargeDuration = 0.4f;
        private const float FireDuration = 0.25f;
        private const float FadeDuration = 0.25f;
        private const float BeamActiveDuration = FireDuration + FadeDuration; // 0.5s
                                                                              // ★ 한 번 빔당 전체 HP에서 깎을 비율 (예: 0.20f = 20%)
        private const float DamageFraction = 0.20f;
        // ───────────── 타겟 / 대미지 계획 ─────────────
        private Health _targetHealth;
        private bool _hasTarget = false;
        private float _plannedTotalDamage = 0f; // MaxHP / 3
        private float _appliedDamage = 0f; // 누적
        private float _startHealth;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            // 마우스 가운데 버튼(휠 클릭)으로 발동
            if (Input.GetMouseButtonDown(2))
            {
                TriggerBeam();
            }

            if (_state == BeamState.Off)
                return;

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) dt = Time.deltaTime;
            if (dt <= 0f) dt = 0.016f;

            _stateTimer += dt;

            switch (_state)
            {
                case BeamState.Charge:
                    if (_stateTimer >= ChargeDuration)
                    {
                        _state = BeamState.Fire;
                        _stateTimer = 0f;
                        _beamLength = 0f;
                        _beamAlpha = 1f;

                        // Fire 진입 시점에 타겟 고정 + 총 대미지(HP 1/3) 계획
                        AcquireBeamTargetAndPlanDamage();
                    }
                    break;

                case BeamState.Fire:
                    {
                        // 빔 길이 애니메이션
                        float t = Mathf.Clamp01(_stateTimer / FireDuration);
                        _beamLength = Mathf.Lerp(0f, Screen.height, t);

                        // Fire 구간에서 지속 대미지
                        ApplyBeamDamageOverTime(dt);

                        if (_stateTimer >= FireDuration)
                        {
                            _state = BeamState.Fade;
                            _stateTimer = 0f;
                        }
                    }
                    break;

                case BeamState.Fade:
                    {
                        float t = Mathf.Clamp01(_stateTimer / FadeDuration);
                        _beamAlpha = 1f - t;

                        // Fade 구간에서도 남은 시간 동안 계속 대미지
                        ApplyBeamDamageOverTime(dt);

                        if (_beamAlpha <= 0.01f)
                        {
                            _state = BeamState.Off;
                            _stateTimer = 0f;
                            _beamLength = 0f;
                            _beamAlpha = 1f;

                            _hasTarget = false;
                            _targetHealth = null;
                            _plannedTotalDamage = 0f;
                            _appliedDamage = 0f;
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// 빔 발동 (입력 트리거)
        /// </summary>
        public void TriggerBeam()
        {
            EnsureTexturesLoaded();

            _state = BeamState.Charge;
            _stateTimer = 0f;
            _beamLength = 0f;
            _beamAlpha = 1f;

            _hasTarget = false;
            _targetHealth = null;
            _plannedTotalDamage = 0f;
            _appliedDamage = 0f;
            _startHealth = 0f;

            // ───────── 해골 UI 위치를 랜덤으로 결정 ─────────
            try
            {
                float margin = 80f; // 화면 끝에서 여유
                float minX = margin;
                float maxX = Screen.width - margin - SKULL_WIDTH;
                float minY = margin;
                float maxY = Screen.height - margin - SKULL_HEIGHT;

                if (maxX < minX) { maxX = minX; }
                if (maxY < minY) { maxY = minY; }

                _skullPosX = UnityEngine.Random.Range(minX, maxX);
                _skullPosY = UnityEngine.Random.Range(minY, maxY);
            }
            catch (Exception ex)
            {
                Debug.Log("[skullbeam] 랜덤 해골 위치 계산 예외: " + ex);
                // 실패하면 예전처럼 화면 위 가운데
                _skullPosX = -1f;
                _skullPosY = -1f;
            }
            // ────────────────────────────────────────────────

            PlayBeamStartSound();

            Debug.Log("[skullbeam] TriggerBeam 발동");
        }


        // ───────────── 텍스처 로드 ─────────────

        private void EnsureTexturesLoaded()
        {
            if (_texturesLoaded)
                return;

            _texturesLoaded = true;

            try
            {
                string dllPath = Assembly.GetExecutingAssembly().Location;
                string dir = Path.GetDirectoryName(dllPath);

                LoadTextureSafe(Path.Combine(dir, "skull_idle.png"), ref _skullIdle);
                LoadTextureSafe(Path.Combine(dir, "skull_charge.png"), ref _skullCharge);
                LoadTextureSafe(Path.Combine(dir, "skull_fire.png"), ref _skullFire);

                Debug.Log("[skullbeam] 해골 텍스처 로드 시도 완료");
            }
            catch (Exception ex)
            {
                Debug.Log("[skullbeam] 텍스처 로드 예외: " + ex);
            }
        }

        private static void LoadTextureSafe(string path, ref Texture2D target)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Debug.Log("[skullbeam] 텍스처 파일 없음: " + path);
                    return;
                }

                byte[] bytes = File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                // UnityEngine.ImageConversionModule.dll 필요
                tex.LoadImage(bytes);
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Point;

                target = tex;
                Debug.Log("[skullbeam] LoadTextureSafe OK: " + path);
            }
            catch (Exception ex)
            {
                Debug.Log("[skullbeam] LoadTextureSafe 예외 (" + path + "): " + ex);
            }
        }

        // ───────────── 사운드 (mikumikubeam 방식) ─────────────

        private void PlayBeamStartSound()
        {
            try
            {
                string dllPath = Assembly.GetExecutingAssembly().Location;
                string folder = Path.GetDirectoryName(dllPath);
                string audioDir = Path.Combine(folder, "Audio");
                string filePath = Path.Combine(audioDir, "BeamStart.mp3");

                if (!File.Exists(filePath))
                {
                    Debug.Log("[skullbeam] BeamStart.mp3 not found: " + filePath);
                    return;
                }

                AudioManager.PostCustomSFX(filePath, null, false);
            }
            catch (Exception ex)
            {
                Debug.LogError("[skullbeam] PlayBeamStartSound ERROR: " + ex.Message);
            }
        }

        // ───────────── 타겟 고정 + HP 1/3 대미지 계획 ─────────────

        private void AcquireBeamTargetAndPlanDamage()
        {
            _hasTarget = false;
            _targetHealth = null;
            _plannedTotalDamage = 0f;
            _appliedDamage = 0f;

            try
            {
                Camera cam = Camera.main;
                if (cam == null)
                {
                    Debug.Log("[skullbeam] AcquireBeamTarget: Camera.main 없음");
                    return;
                }

                Ray ray = cam.ScreenPointToRay(Input.mousePosition);

                float sphereRadius = 1.5f;  // 커서 근처 허용 반경
                float maxDistance = 9999f;

                RaycastHit[] hits = Physics.SphereCastAll(ray, sphereRadius, maxDistance);
                if (hits == null || hits.Length == 0)
                {
                    Debug.Log("[skullbeam] AcquireBeamTarget: SphereCastAll hit 없음");
                    return;
                }

                float bestDist = float.MaxValue;
                Health bestHealth = null;

                foreach (var h in hits)
                {
                    GameObject obj = h.collider.gameObject;

                    if (IsPlayerObject(obj))
                    {
                        Debug.Log("[skullbeam] AcquireBeamTarget: 플레이어 콜라이더 스킵 (" + obj.name + ")");
                        continue;
                    }

                    Health hpCheck = obj.GetComponentInParent<Health>();
                    if (hpCheck == null)
                        continue;

                    if (h.distance < bestDist)
                    {
                        bestDist = h.distance;
                        bestHealth = hpCheck;
                    }
                }

                if (bestHealth == null)
                {
                    Debug.Log("[skullbeam] AcquireBeamTarget: 유효한 적 타겟을 찾지 못함");
                    return;
                }

                float approxMaxHp = GetApproxMaxHealth(bestHealth);
                if (approxMaxHp <= 0f)
                    approxMaxHp = 100f;

                _targetHealth = bestHealth;
                _hasTarget = true;
                _plannedTotalDamage = approxMaxHp * DamageFraction; // ★ 한 번 빔당 HP의 일정 비율
                _appliedDamage = 0f;

                Debug.Log("[skullbeam] 타겟 고정: obj=" + bestHealth.gameObject.name +
                          " dist=" + bestDist.ToString("F2") +
                          " approxMaxHp=" + approxMaxHp.ToString("F1") +
                          " plannedTotalDmg=" + _plannedTotalDamage.ToString("F1"));
            }
            catch (Exception ex)
            {
                Debug.Log("[skullbeam] AcquireBeamTargetAndPlanDamage 예외: " + ex);
            }
        }

        private float GetApproxMaxHealth(Health health)
        {
            if (health == null) return 0f;

            Type t = health.GetType();

            try
            {
                // 1) MaxHealth 프로퍼티
                PropertyInfo pMax = t.GetProperty("MaxHealth", BINDING_FLAGS);
                if (pMax != null)
                {
                    object v = pMax.GetValue(health, null);
                    float f = Convert.ToSingle(v);
                    if (f > 0f) return f;
                }
            }
            catch { }

            try
            {
                // 2) maxHealth 필드
                FieldInfo fMax = t.GetField("maxHealth", BINDING_FLAGS);
                if (fMax != null)
                {
                    object v = fMax.GetValue(health);
                    float f = Convert.ToSingle(v);
                    if (f > 0f) return f;
                }
            }
            catch { }

            try
            {
                // 3) CurrentHealth 프로퍼티 (현재 체력으로 대충 추정)
                PropertyInfo pCur = t.GetProperty("CurrentHealth", BINDING_FLAGS);
                if (pCur != null)
                {
                    object v = pCur.GetValue(health, null);
                    float f = Convert.ToSingle(v);
                    if (f > 0f) return f;
                }
            }
            catch { }

            try
            {
                // 4) health / hp 관련 필드 중 하나
                FieldInfo[] fields = t.GetFields(BINDING_FLAGS);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo f = fields[i];
                    if (f == null) continue;
                    Type ft = f.FieldType;
                    if (ft != typeof(float) && ft != typeof(int) &&
                        ft != typeof(double) && ft != typeof(short))
                        continue;

                    string n = f.Name.ToLower();
                    if ((n.Contains("health") || n == "hp" || n.Contains("current")) &&
                        !n.Contains("max") && !n.Contains("hash") && !n.Contains("height"))
                    {
                        object v = f.GetValue(health);
                        float val = Convert.ToSingle(v);
                        if (val > 0f) return val;
                    }
                }
            }
            catch { }

            return 0f;
        }

        // ───────────── 지속 대미지 적용 (총합 1/3 HP) ─────────────

        private void ApplyBeamDamageOverTime(float deltaTime)
        {
            if (!_hasTarget || _targetHealth == null)
                return;

            if (_plannedTotalDamage <= 0f)
                return;

            float remaining = _plannedTotalDamage - _appliedDamage;
            if (remaining <= 0f)
                return;

            if (deltaTime <= 0f)
                return;

            float damageRatePerSecond = _plannedTotalDamage / BeamActiveDuration;
            float tickDamage = damageRatePerSecond * deltaTime;
            if (tickDamage > remaining)
                tickDamage = remaining;

            if (tickDamage <= 0f)
                return;

            bool ok = ApplyDamageTick(_targetHealth, tickDamage);
            if (!ok)
            {
                // Hurt 계열이 전혀 없는 특이 케이스면, 숫자 필드만 줄여본다.
                ok = ApplyDamageDirectToHealthNumbers(_targetHealth, tickDamage);
            }

            if (!ok)
            {
                // 더 이상 대미지 줄 수 없으면 타겟 해제
                Debug.Log("[skullbeam] ApplyBeamDamageOverTime: 대미지 적용 실패, 타겟 해제");
                _hasTarget = false;
                _targetHealth = null;
                return;
            }

            _appliedDamage += tickDamage;
        }

        /// <summary>
        /// Hurt(DamageInfo) / Hurt(float/int) 등으로 tickDamage 만큼 넣기
        /// </summary>
        private bool ApplyDamageTick(Health health, float damage)
        {
            if (health == null) return false;

            try
            {
                Type type = health.GetType();

                MethodInfo hurt = type.GetMethod("Hurt", BINDING_FLAGS);
                if (hurt == null)
                    hurt = type.GetMethod("Hurt");

                if (hurt == null)
                {
                    return false;
                }

                ParameterInfo[] ps = hurt.GetParameters();

                // 1) Hurt(DamageInfo)
                if (ps.Length == 1 && ps[0].ParameterType.Name == "DamageInfo")
                {
                    Type damageType = ps[0].ParameterType;
                    object dmgInfo = Activator.CreateInstance(damageType);

                    // damageValue / finalDamage
                    FieldInfo fDamageValue = damageType.GetField("damageValue", BINDING_FLAGS);
                    if (fDamageValue != null)
                    {
                        if (fDamageValue.FieldType == typeof(float)) fDamageValue.SetValue(dmgInfo, damage);
                        else if (fDamageValue.FieldType == typeof(int)) fDamageValue.SetValue(dmgInfo, (int)Math.Ceiling(damage));
                    }

                    FieldInfo fFinalDamage = damageType.GetField("finalDamage", BINDING_FLAGS);
                    if (fFinalDamage != null)
                    {
                        if (fFinalDamage.FieldType == typeof(float)) fFinalDamage.SetValue(dmgInfo, damage);
                        else if (fFinalDamage.FieldType == typeof(int)) fFinalDamage.SetValue(dmgInfo, (int)Math.Ceiling(damage));
                    }

                    // fromCharacter = 플레이어
                    FieldInfo fFromChar = damageType.GetField("fromCharacter", BINDING_FLAGS);
                    if (fFromChar != null)
                    {
                        CharacterMainControl mainChar = FindMainCharacter();
                        if (mainChar != null && fFromChar.FieldType.IsAssignableFrom(mainChar.GetType()))
                        {
                            fFromChar.SetValue(dmgInfo, mainChar);
                        }
                    }

                    // ignoreArmor / ignoreDifficulty true
                    FieldInfo fIgnoreArmor = damageType.GetField("ignoreArmor", BINDING_FLAGS);
                    if (fIgnoreArmor != null && fIgnoreArmor.FieldType == typeof(bool))
                    {
                        fIgnoreArmor.SetValue(dmgInfo, true);
                    }

                    FieldInfo fIgnoreDiff = damageType.GetField("ignoreDifficulty", BINDING_FLAGS);
                    if (fIgnoreDiff != null && fIgnoreDiff.FieldType == typeof(bool))
                    {
                        fIgnoreDiff.SetValue(dmgInfo, true);
                    }

                    // damagePoint = 보스 위치
                    FieldInfo fDamagePoint = damageType.GetField("damagePoint", BINDING_FLAGS);
                    if (fDamagePoint != null && fDamagePoint.FieldType == typeof(Vector3))
                    {
                        Vector3 point = Vector3.zero;
                        try { point = health.transform.position; } catch { }
                        fDamagePoint.SetValue(dmgInfo, point);
                    }

                    // elementFactors 같은 추가 필드 있으면 기본 인스턴스 넣기
                    FieldInfo fElementFactors = damageType.GetField("elementFactors", BINDING_FLAGS);
                    if (fElementFactors != null)
                    {
                        try
                        {
                            Type efType = fElementFactors.FieldType;
                            if (efType != null && efType.GetConstructor(Type.EmptyTypes) != null)
                            {
                                object efInstance = Activator.CreateInstance(efType);
                                fElementFactors.SetValue(dmgInfo, efInstance);
                            }
                        }
                        catch { }
                    }

                    hurt.Invoke(health, new object[] { dmgInfo });
                    return true;
                }

                // 2) Hurt( float / int ) 한 개짜리
                if (ps.Length == 1)
                {
                    object dmg = null;
                    if (ps[0].ParameterType == typeof(float))
                        dmg = damage;
                    else if (ps[0].ParameterType == typeof(int))
                        dmg = (int)Math.Ceiling(damage);

                    if (dmg != null)
                    {
                        hurt.Invoke(health, new object[] { dmg });
                        return true;
                    }
                }

                // 3) Hurt(dmg, something...)
                if (ps.Length >= 2)
                {
                    object[] args = new object[ps.Length];
                    if (ps[0].ParameterType == typeof(float))
                        args[0] = damage;
                    else if (ps[0].ParameterType == typeof(int))
                        args[0] = (int)Math.Ceiling(damage);
                    else
                        args[0] = null;

                    // 나머지 파라미터는 null/기본값
                    for (int i = 1; i < ps.Length; i++)
                    {
                        if (ps[i].ParameterType == typeof(Vector3))
                            args[i] = Vector3.zero;
                        else
                            args[i] = Type.Missing;
                    }

                    hurt.Invoke(health, args);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.Log("[skullbeam] ApplyDamageTick 예외: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Hurt가 전혀 없을 때, 숫자 필드만 줄이는 최후의 수단
        /// </summary>
        private bool ApplyDamageDirectToHealthNumbers(Health health, float damage)
        {
            if (health == null) return false;

            try
            {
                Type t = health.GetType();

                // 1) CurrentHealth 프로퍼티
                PropertyInfo pCur = t.GetProperty("CurrentHealth", BINDING_FLAGS);
                if (pCur != null && pCur.CanRead && pCur.CanWrite)
                {
                    object v = pCur.GetValue(health, null);
                    float cur = Convert.ToSingle(v);
                    float next = cur - damage;
                    if (next < 0f) next = 0f;

                    if (pCur.PropertyType == typeof(float))
                        pCur.SetValue(health, next, null);
                    else if (pCur.PropertyType == typeof(int))
                        pCur.SetValue(health, (int)Math.Floor(next), null);

                    return true;
                }

                // 2) health/hp 필드
                FieldInfo[] fields = t.GetFields(BINDING_FLAGS);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo f = fields[i];
                    if (f == null) continue;

                    Type ft = f.FieldType;
                    if (ft != typeof(float) && ft != typeof(int) &&
                        ft != typeof(double) && ft != typeof(short))
                        continue;

                    string n = f.Name.ToLower();
                    if (!(n.Contains("health") || n == "hp" || n.Contains("current")) ||
                        n.Contains("max") || n.Contains("hash") || n.Contains("height"))
                        continue;

                    object v = f.GetValue(health);
                    float cur = Convert.ToSingle(v);
                    float next = cur - damage;
                    if (next < 0f) next = 0f;

                    if (ft == typeof(float))
                        f.SetValue(health, next);
                    else if (ft == typeof(int))
                        f.SetValue(health, (int)Math.Floor(next));
                    else if (ft == typeof(double))
                        f.SetValue(health, (double)next);
                    else if (ft == typeof(short))
                        f.SetValue(health, (short)Math.Max(0, (int)Math.Floor(next)));

                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[skullbeam] ApplyDamageDirectToHealthNumbers 예외: " + ex);
            }

            return false;
        }

        private CharacterMainControl FindMainCharacter()
        {
            try
            {
                // 가장 무난한 Main 캐릭터 찾기
                if (CharacterMainControl.Main != null)
                    return CharacterMainControl.Main;

                CharacterMainControl[] all =
                    UnityEngine.Object.FindObjectsOfType<CharacterMainControl>();
                if (all != null && all.Length > 0)
                    return all[0];
            }
            catch (Exception ex)
            {
                Debug.Log("[skullbeam] FindMainCharacter 예외: " + ex);
            }

            return null;
        }

        /// <summary>
        /// 플레이어/카메라 쪽 오브젝트인지 대충 필터링 (플레이어 보호용)
        /// </summary>
        private bool IsPlayerObject(GameObject obj)
        {
            if (obj == null) return true;

            try
            {
                if (CharacterMainControl.Main != null)
                {
                    GameObject mainObj = CharacterMainControl.Main.gameObject;
                    if (obj == mainObj || obj.transform.IsChildOf(mainObj.transform))
                        return true;
                }

                if (Camera.main != null)
                {
                    if (obj.transform.IsChildOf(Camera.main.transform.root))
                        return true;
                }

                string n = obj.name.ToLower();
                if (n.Contains("player") || n.Contains("local"))
                    return true;
            }
            catch (Exception ex)
            {
                Debug.Log("[skullbeam] IsPlayerObject 예외: " + ex);
            }

            return false;
        }

        // ───────────── HUD (커서 방향 회전 + 빔) ─────────────

        private void OnGUI()
        {
            if (_state == BeamState.Off)
                return;

            EnsureTexturesLoaded();

            float skullW = SKULL_WIDTH;
            float skullH = SKULL_HEIGHT;

            // 트리거에서 랜덤 좌표를 못 정했으면(초기값) 예전처럼 화면 위 가운데 사용
            float skullX;
            float skullY;

            if (_skullPosX < 0f || _skullPosY < 0f)
            {
                skullX = Screen.width * 0.5f - skullW * 0.5f;
                skullY = 20f;
            }
            else
            {
                skullX = _skullPosX;
                skullY = _skullPosY;
            }

            // 해골 중심 기준
            float centerX = skullX + skullW * 0.5f;
            float beamStartY = skullY + skullH * 0.6f;
            Vector2 pivotGui = new Vector2(centerX, beamStartY);


            // GUI 좌표계 기준 마우스 위치 (y 뒤집기)
            Vector2 mouseGui = new Vector2(
                Input.mousePosition.x,
                Screen.height - Input.mousePosition.y
            );
            Vector2 dirGui = mouseGui - pivotGui;

            float rotationDeg = 0f;
            if (dirGui.sqrMagnitude > 0.0001f)
            {
                float angleGuiDeg = Mathf.Atan2(dirGui.y, dirGui.x) * Mathf.Rad2Deg;
                // 기본 빔이 “아래쪽(0,+1)” 이라서 커서 방향으로 돌리려면 -90도
                rotationDeg = angleGuiDeg - 90f;
            }

            Matrix4x4 oldMatrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(rotationDeg, pivotGui);

            // ── 해골 ──
            Rect skullRect = new Rect(skullX, skullY, skullW, skullH);

            Texture2D skullTex = _skullIdle;
            if (_state == BeamState.Charge && _skullCharge != null)
                skullTex = _skullCharge;
            else if ((_state == BeamState.Fire || _state == BeamState.Fade) && _skullFire != null)
                skullTex = _skullFire;

            if (skullTex != null)
                GUI.DrawTexture(skullRect, skullTex, ScaleMode.ScaleToFit, true);
            else
                GUI.Box(skullRect, "☠");

            // ── 빔 ──
            if ((_state == BeamState.Fire || _state == BeamState.Fade) && _beamLength > 0.0f)
            {
                float coreWidth = 18f;
                float glowWidth = 60f;

                Texture2D beamTex = Texture2D.whiteTexture;
                Color oldColor = GUI.color;

                // 바깥 글로우
                GUI.color = new Color(0.2f, 0.8f, 1.0f, _beamAlpha * 0.30f);
                Rect glowRect = new Rect(
                    centerX - glowWidth * 0.5f,
                    beamStartY,
                    glowWidth,
                    _beamLength
                );
                GUI.DrawTexture(glowRect, beamTex, ScaleMode.StretchToFill, true);

                // 안쪽 코어
                GUI.color = new Color(0.6f, 0.9f, 1.0f, _beamAlpha * 0.90f);
                Rect coreRect = new Rect(
                    centerX - coreWidth * 0.5f,
                    beamStartY,
                    coreWidth,
                    _beamLength
                );
                GUI.DrawTexture(coreRect, beamTex, ScaleMode.StretchToFill, true);

                GUI.color = oldColor;
            }

            GUI.matrix = oldMatrix;
        }
    }
}
