# MEDIUM Severity Fix Plans — 2026-05-22

**Scope:** [`2026-05-22-medium-status-review.md`](./2026-05-22-medium-status-review.md) içinde OPEN olarak işaretlenen bulgular için somut fix planları.

**Önemli düzeltme:** Önceki MEDIUM review'da 11 OPEN sayılmıştı, ancak **unit-12 #3 (Namespace Injection)** ek doğrulamada **FIXED** çıktı. `FileGenerationStep.cs:28` regex `^[A-Za-z_][\w]*(\.[A-Za-z_][\w]*)*$` — anchored, whitelist-only; `{`, `}`, `;` gibi karakterler eşleşmez. Yani **gerçek OPEN sayısı 10**.

**Format:** Her plan için: hedef dosya, mevcut kod, somut değişiklik (kod snippet), breaking risk, effort, test yaklaşımı, dependency, kabul kriteri.

**Plan tablosu** (kolay→zor):

| # | Bulgu | Effort | Breaking | Sıra |
|---|-------|-------:|----------|-----:|
| F1 | unit-12 #5 — TargetPath bypass | **30 dk** | Yok | 1 |
| F2 | DI-11 — Transient IDisposable leak | **2-3 sa** | Davranışsal | 2 |
| F3 | unit-04 #8 — SparseSet overflow | **30 dk** | Yok | 3 |
| F4 | unit-05 #6 — ECB thread-safety doc | **1 sa** | Yok (sadece doc) | 4 |
| F5 | DI-08 — DirectFactory tampering | **1-2 sa** | Hayır (internal field zaten yok) | 5 |
| F6 | unit-03 #2 — Permissive default patterns | **2 sa** | API: default davranış değişir | 6 |
| F7 | MOD-03 — SerializableType validation | **3-4 sa** | Edge: existing assets warning | 7 |
| F8 | unit-09 #04 — EventBus handler tokens | **1 gün** | **Major API** | 8 |
| F9 | unit-11 SYNC-02 — ReactiveProperty BindingScope | **1 gün** | **Major API** | 9 |
| F10 | unit-16 #1 — SourceGen upper-bound | **1-2 gün** (perf ölçüm dahil) | Generated kod — perf etkisi | 10 |

---

## F1 — unit-12 #5: `SetTargetPath` Validation Bypass

**Severity:** MEDIUM • **Effort:** ~30 dk • **Breaking:** Yok

### Hedef dosya
`Editor/ModuleGenerator/StradaModuleGenerator.cs:88-92`

### Mevcut kod
```csharp
public void SetTargetPath(string path)
{
    if (_moduleDefinition != null)
        _moduleDefinition.TargetPath = path;
}
```
UI input için `ValidateTargetPath()` (`StradaModuleGenerator.Validation.cs:125`) çağrılırken (path required, Assets içinde olmalı, conflict check), `SetTargetPath` programatik atamada bunu atlatıyor. `StradaModuleGenerator.cs:63` (`window.SetTargetPath(path)`) bu yolla validation atlayabiliyor.

### Fix
```csharp
public void SetTargetPath(string path)
{
    if (_moduleDefinition == null) return;
    _moduleDefinition.TargetPath = path;
    ValidateAll();  // already calls ValidateTargetPath internally
}
```

Eğer `ValidateAll()` UI repaint tetikliyorsa ve programatik caller'lar (örn. menu item) tetiklemek istemiyorsa: opsiyonel bool parametre ekle:
```csharp
public void SetTargetPath(string path, bool validate = true)
{
    if (_moduleDefinition == null) return;
    _moduleDefinition.TargetPath = path;
    if (validate) ValidateTargetPath();
}
```

### Test
- `Strada/Generate Module` menü → context-menu'dan klasör üzerinde aç → path'in `Assets/` dışında olduğunu test et → validation error gelmeli.
- Manuel test: `SetTargetPath("../../etc")` → ValidationMessage.Error("must be within Assets") tetiklenmeli.

### Kabul kriteri
`SetTargetPath` çağrısından sonra `_validationMessages` listesi içinde TargetPath ile ilgili validation entry'leri güncellenmiş olmalı.

---

## F2 — DI-11: Transient `IDisposable` Services Never Disposed

**Severity:** MEDIUM • **Effort:** 2-3 saat • **Breaking:** Davranışsal — transient'lar artık container ile birlikte dispose olur

### Hedef dosya
`Runtime/DI/Container.cs:328-330`

### Mevcut kod
```csharp
else
{
    _factories[index] = rawFactory;  // transient: hiçbir tracking yok
}
```

### Fix
Transient lifetime için, üretilen instance `IDisposable` ise `_disposalStack`'e ekle:

```csharp
else
{
    // Transient: track IDisposable instances so they're disposed with the container
    _factories[index] = _ =>
    {
        var instance = rawFactory(this);
        if (instance is IDisposable disposable)
        {
            lock (_lock)
            {
                if (_disposed) { disposable.Dispose(); throw new ObjectDisposedException(nameof(Container)); }
                _disposalStack.Push(disposable);
            }
        }
        return instance;
    };
}
```

### Önemli not — bellek davranışı değişir
Mevcut davranış: transient `IDisposable` instance'ları **caller** dispose etmek zorunda. Yeni davranış: container kendisi de tutuyor (`_disposalStack`). Bu, eğer transient çok sık resolve ediliyorsa **bellek büyür** (örn. her frame yeni `Disposable` üretilirse leak'e benzer).

**Alternatif (güvenli ama opt-in):** Sadece `[TrackTransientDisposal]` attribute ile işaretli tipler için track et. Veya `ContainerBuilder` üzerinde flag:

```csharp
builder.Register<MyService>(Lifetime.Transient).WithDisposalTracking();
```

### Test
```csharp
[Test]
public void Transient_IDisposable_DisposedWithContainer()
{
    var disposed = false;
    var container = new ContainerBuilder()
        .Register<MyService>(() => new MyService(() => disposed = true), Lifetime.Transient)
        .Build();

    var instance = container.Resolve<MyService>();
    container.Dispose();
    Assert.IsTrue(disposed);
}
```

### Dependency
Yok.

### Kabul kriteri
Container.Dispose() çağrısı sonrası, container yaşamı boyunca resolve edilmiş tüm transient IDisposable'ların Dispose() metodu LIFO sırada çağrılır.

---

## F3 — unit-04 #8: `SparseSet.EnsureSparseCapacity` Overflow

**Severity:** MEDIUM (uç durum) • **Effort:** ~30 dk • **Breaking:** Yok

### Hedef dosya
`Runtime/ECS/Storage/SparseSet.cs:189-194`

### Mevcut kod
```csharp
int newCapacity = Math.Max(required, _sparse.Length * 3 / 2);
if (newCapacity > MaxSparseCapacity) newCapacity = MaxSparseCapacity;
```
`_sparse.Length * 3` int overflow yapabilir. Pratikte `MaxSparseCapacity = 1_048_576` (1M) cap'i mitigates ama `_sparse.Length` 715M civarına ulaşırsa `* 3` int overflow eder.

### Fix
```csharp
// Use long arithmetic to prevent overflow, then clamp
long grown = (long)_sparse.Length * 3 / 2;
int newCapacity = (int)Math.Min(
    Math.Max(required, grown),
    MaxSparseCapacity);
```

### Test
```csharp
[Test]
public void EnsureSparseCapacity_NearIntMax_DoesNotOverflow()
{
    // Test mental model with smaller MaxSparseCapacity
    // Set internal _sparse.Length close to int.MaxValue / 2 — verify newCapacity stays positive
}
```

### Kabul kriteri
Hiçbir input için negative `newCapacity` üretmemeli; cap her zaman `MaxSparseCapacity`.

---

## F4 — unit-05 #6: EntityCommandBuffer Thread-Safety Documentation

**Severity:** MEDIUM • **Effort:** 1 saat • **Breaking:** Hayır (sadece doc)

### Hedef dosya
`Runtime/ECS/Jobs/EntityCommandBuffer.cs:18-50`

### Mevcut kod
`unsafe struct EntityCommandBuffer` Burst-compiled, ama hiçbir XML doc warning'i yok. Caller'lar bunu paralel job'larda yanlış kullanabilir.

### Fix
Struct'a açıklayıcı XML doc ve `Unity.Jobs.LowLevel.Unsafe` konvansiyonlarına uygun warning ekle:

```csharp
/// <summary>
/// A deferred command buffer for recording ECS structural changes outside of safe contexts.
/// </summary>
/// <remarks>
/// <para><b>THREAD SAFETY:</b> EntityCommandBuffer is <b>not thread-safe</b>. Each instance
/// is intended to be used by a single thread (or a single job). To record commands from
/// multiple threads concurrently, use <c>AsParallelWriter()</c> (TODO) or create one buffer
/// per worker thread and merge them at playback time.</para>
/// <para>Playback (<see cref="Playback"/>) must run on the main thread; it mutates
/// <see cref="EntityManager"/> state which is not thread-safe.</para>
/// </remarks>
[BurstCompile]
public unsafe struct EntityCommandBuffer : IDisposable
{
    // ...
}
```

Roadmap olarak `AsParallelWriter()` API'si planlanıyorsa, doc'ta belirt.

### Test
Doc-only fix — manuel review yeterli. Roslyn analyzer (örn. eksik XML doc) eklenirse CI'de zorla.

### Kabul kriteri
Public API XML doc'unda thread-safety durumu açıkça yazılı.

---

## F5 — DI-08: `DirectFactory<T>.Delegate` Public Static Field

**Severity:** MEDIUM • **Effort:** 1-2 saat • **Breaking:** Iç-API; source-generated kod adapte edilmeli

### Hedef dosyalar
- `Runtime/DI/IStradaFactory.cs:5-8`
- `Runtime/DI/Container.cs:407` (`ClearFactory`)
- Source generator: `SourceGenerationDI~/...` (source-generated registry buraya `Delegate = ...` yazıyor)

### Mevcut kod
```csharp
public static class DirectFactory<T> where T : class
{
    public static Func<IContainer, T> Delegate;  // anyone can overwrite
}
```

### Fix
Field'ı private yap, public Register/Clear API'leri sun:

```csharp
public static class DirectFactory<T> where T : class
{
    private static Func<IContainer, T> _delegate;

    internal static Func<IContainer, T> Get() => _delegate;

    /// <summary>
    /// Registers a direct factory. Throws if one is already registered (use Clear() first).
    /// Intended to be called from generated registry initializer only.
    /// </summary>
    public static void Register(Func<IContainer, T> factory)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        if (Interlocked.CompareExchange(ref _delegate, factory, null) != null)
            throw new InvalidOperationException(
                $"DirectFactory<{typeof(T).Name}> is already registered. Call Clear() first.");
    }

    public static void Clear() => Volatile.Write(ref _delegate, null);
}
```

`Container.cs:407` güncelle:
```csharp
private static void ClearFactory(Type type) =>
    typeof(DirectFactory<>).MakeGenericType(type)
        .GetMethod(nameof(DirectFactory<object>.Clear), BindingFlags.Static | BindingFlags.Public)
        .Invoke(null, null);
```

`Container.cs:CreateDirectFactoryWrapper<T>` da `DirectFactory<T>.Delegate` → `DirectFactory<T>.Get()` olur.

**Source generator etkisi:** Generated code `DirectFactory<MyService>.Delegate = factoryFn;` yerine `DirectFactory<MyService>.Register(factoryFn);` üretmeli. `SourceGenerationDI~` içindeki template güncellenmeli.

### Test
```csharp
[Test]
public void DirectFactory_Register_ThrowsOnDouble() { ... }

[Test]
public void DirectFactory_Cleared_BetweenContainers() { ... }
```

### Dependency
Source generator template ile birlikte release edilmeli (atomik PR).

### Kabul kriteri
Test projesinde `DirectFactory<X>.Delegate = ...` compile etmemeli (alan private).

---

## F6 — unit-03 #2: AutoBinding Permissive Default Patterns

**Severity:** MEDIUM • **Effort:** ~2 saat • **Breaking:** Default davranış değişir

### Hedef dosya
`Runtime/DI/AutoBinding/RuntimeAutoBindingScanner.cs:19`

### Mevcut kod
```csharp
private static readonly string[] DefaultIncludePatterns = { "Strada.*", "Game.*", "Assembly-CSharp" };
```

`Game.*` herhangi bir `Game.MaliciousMod.dll`'i tarar; `Assembly-CSharp` ise Unity default user code'unu kapsar — pattern bir güvenlik sınırı değil.

### Fix (iki opsiyon)

**Opsiyon A (önerilen): Defaults'tan `Game.*` çıkar, opt-in attribute zorla.**

```csharp
// Pattern güvenlik sınırı değil; opt-in için [AutoBindingScope] attribute kullanılmalı
private static readonly string[] DefaultIncludePatterns = { "Strada.*", "Assembly-CSharp" };
```

Sonra her game-specific assembly'ye assembly-level attribute eklenir:
```csharp
[assembly: Strada.Core.DI.AutoBinding.AutoBindingScope]
```

Tarama da include pattern + bu attribute'un birleşimini gerektirir.

**Opsiyon B: `Game.*`'yi tut ama explicit config gereksin.**
`includePatterns ??= DefaultIncludePatterns;` yerine `includePatterns ??= throw new InvalidOperationException("Specify include patterns explicitly")`. Bu daha agresif, mevcut kullanıcıları kırar.

### Migration plan (Opsiyon A için)
1. `[AutoBindingScope]` attribute'unu Runtime'da tanıt
2. `DefaultIncludePatterns`'ı koru ama deprecation warning ekle:
```csharp
foreach (var asm in assemblies.Where(a => MatchesAnyPattern(a.GetName().Name, includePatterns)))
{
    if (!HasAutoBindingScopeAttribute(asm))
        Debug.LogWarning($"[AutoBinding] Assembly '{asm.GetName().Name}' matches include pattern but lacks [AutoBindingScope]. This will be a hard error in v4.");
}
```
3. Sonraki major version'da hard error yap.

### Test
Test assembly'sine `[assembly: AutoBindingScope]` ekle, scan'da görünmeli. Eklemezse görünmemeli.

### Kabul kriteri
Bilinmeyen bir `Game.X` assembly attribute olmadan otomatik bind edilmez.

---

## F7 — MOD-03: `SerializableType.Type.GetType` Validation

**Severity:** MEDIUM • **Effort:** 3-4 saat • **Breaking:** Edge case (mevcut asset'ler warning verir)

### Hedef dosya
`Runtime/Modules/SerializableType.cs:24-31`

### Mevcut kod
```csharp
_cachedType = Type.GetType(_assemblyQualifiedName);
if (_cachedType == null)
    Debug.LogWarning($"[SerializableType] Failed to resolve type: {_assemblyQualifiedName}");
```
Resolved type üzerinde hiçbir constraint/allowlist yok.

### Fix
Allowlist mekanizması ekle — generic `SerializableType<TBase>` ile expected base type'ı tip parametresi olarak al:

```csharp
[Serializable]
public class SerializableType<TBase> : ISerializationCallbackReceiver where TBase : class
{
    [SerializeField] private string _assemblyQualifiedName;
    private Type _cachedType;
    private bool _resolveAttempted;

    public Type Type
    {
        get
        {
            if (_cachedType != null) return _cachedType;
            if (_resolveAttempted || string.IsNullOrEmpty(_assemblyQualifiedName)) return null;

            _resolveAttempted = true;
            var resolved = Type.GetType(_assemblyQualifiedName);
            if (resolved == null)
            {
                Debug.LogWarning($"[SerializableType] Failed to resolve: {_assemblyQualifiedName}");
                return null;
            }
            if (!typeof(TBase).IsAssignableFrom(resolved))
            {
                Debug.LogError($"[SerializableType] Type '{resolved.FullName}' does not derive from {typeof(TBase).Name} — rejected.");
                return null;
            }
            _cachedType = resolved;
            return _cachedType;
        }
        set { /* validate before set */ }
    }
}
```

Mevcut `SerializableType` non-generic class'ı **obsolete** olarak işaretle:
```csharp
[Obsolete("Use SerializableType<TBase> with an expected base type for type safety.", error: false)]
public class SerializableType : SerializableType<object> { }
```

### Çağrı sitelerini güncelle
- `Runtime/Modules/SystemRunner.cs:265` — `SerializableType<ISystem>` kullan
- `Runtime/Modules/ModuleRegistry.cs:132` — `SerializableType<IModuleInstaller>`
- ModuleConfig ScriptableObject schema'ları güncellenecek (asset migration gerekli)

### Asset migration
Mevcut `SerializableType` field'ları olan ScriptableObject asset'leri:
- Editor-side migration script yaz: tüm `.asset` dosyalarını tara, `_assemblyQualifiedName` resolved tip kontrolünden geçiyor mu kontrol et, geçmeyenler için error log üret.

### Test
- Geçersiz asset (örn. attacker bir tipi `_assemblyQualifiedName` olarak yazdı): resolve `null` döner, error log.
- Doğru asset: cache'lenir, normal davranır.

### Dependency
`SystemRunner` ve `ModuleRegistry` aynı PR'da güncellenmeli (asset schema değişiyor).

### Kabul kriteri
ScriptableObject asset'inde `_assemblyQualifiedName` `TBase`'den türemeyen bir tipi gösterirse `Type` getter `null` döner ve error logger.

---

## F8 — unit-09 #04: EventBus Handler Lifecycle Token

**Severity:** MEDIUM • **Effort:** ~1 gün • **Breaking:** Major API

### Hedef dosya
`Runtime/Communication/EventBus.cs:159-263` (Signal, Query, Event subscription path)

### Mevcut kod
```csharp
public void RegisterSignalHandler<TSignal>(Action<TSignal> handler) where TSignal : struct
{
    // tek slot — handler aynı tip için silinmek istenirse tüm tipi temizler
}

public void UnregisterSignalHandler<TSignal>() where TSignal : struct
{
    // tek tip için tüm handler'ı kaldırır
}
```

### Fix
`IDisposable` token döndüren yeni API ekle (mevcut API'yi koru, deprecation warning ile):

```csharp
public sealed class SubscriptionToken : IDisposable
{
    private Action _dispose;
    internal SubscriptionToken(Action dispose) => _dispose = dispose;
    public void Dispose()
    {
        var d = Interlocked.Exchange(ref _dispose, null);
        d?.Invoke();
    }
}

public SubscriptionToken Subscribe<TSignal>(Action<TSignal> handler) where TSignal : struct
{
    RegisterSignalHandler(handler);  // current behavior
    return new SubscriptionToken(() => UnregisterSignalHandler<TSignal>());
}
```

**Daha doğru:** Tek slot pattern yerine handler **list** tut. Her Subscribe yeni slot, her token Dispose yalnızca o slot'u kaldırır. Mevcut "tek handler per signal" semantiği değiştiği için major API change.

Eğer tek-handler semantiği korunacaksa: Subscribe iki kez aynı tip için çağrılırsa eski token invalidate edilir.

### Migration
1. v3.x: `Subscribe` + `SubscriptionToken` API eklenir, eski `RegisterSignalHandler` korunur ama `[Obsolete]`.
2. v4.0: Eski API kaldırılır.

### Test
```csharp
[Test]
public void Subscribe_Dispose_RemovesHandler()
{
    var bus = new EventBus();
    int calls = 0;
    using (bus.Subscribe<MySignal>(_ => calls++)) {
        bus.Send(new MySignal());
    }
    bus.Send(new MySignal());
    Assert.AreEqual(1, calls);
}
```

### Dependency
F9 (SYNC-02) ile aynı pattern; ortak `SubscriptionToken` tipi paylaşılabilir → tek PR halinde halletmek mantıklı.

### Kabul kriteri
`token.Dispose()` çağrısı yalnızca o handler'ı kaldırır; aynı sinyalin diğer subscribers etkilenmez.

---

## F9 — unit-11 SYNC-02: ReactiveProperty BindingScope

**Severity:** MEDIUM • **Effort:** ~1 gün • **Breaking:** Major API (mevcut `Subscribe` void)

### Hedef dosya
`Runtime/Sync/ReactiveProperty.cs:50-58`

### Mevcut kod
```csharp
public interface IReadOnlyReactiveProperty<T>
{
    T Value { get; }
    void Subscribe(Action<T> handler);
    void Unsubscribe(Action<T> handler);
}

public void Subscribe(Action<T> handler) => _handlers.Add(handler);
```

### Fix
F8 ile uyumlu `SubscriptionToken` döndür:

```csharp
public interface IReadOnlyReactiveProperty<T>
{
    T Value { get; }
    SubscriptionToken Subscribe(Action<T> handler);            // yeni
    void Unsubscribe(Action<T> handler);                       // deprecated
}

public SubscriptionToken Subscribe(Action<T> handler)
{
    _handlers.Add(handler);
    return new SubscriptionToken(() => Unsubscribe(handler));
}
```

`SubscribeAndInvoke` da aynı pattern.

### BindingScope helper (öneri)
`Runtime/Sync/BindingScope.cs` (yeni dosya):
```csharp
public sealed class BindingScope : IDisposable
{
    private readonly List<IDisposable> _tokens = new();
    public void Add(IDisposable token) => _tokens.Add(token);
    public void Dispose()
    {
        for (int i = _tokens.Count - 1; i >= 0; i--) _tokens[i].Dispose();
        _tokens.Clear();
    }
}

public static class BindingScopeExtensions
{
    public static void AddTo(this SubscriptionToken token, BindingScope scope) => scope.Add(token);
}
```

Kullanım:
```csharp
var scope = new BindingScope();
playerHealth.Subscribe(OnHealthChanged).AddTo(scope);
playerScore.Subscribe(OnScoreChanged).AddTo(scope);
// when controller disposed:
scope.Dispose();  // both unsubscribed LIFO
```

### Migration
- v3.x: Yeni `Subscribe` return type değişir → callers compile error alır. Sürüm bump major olmalı.
- Alternatif: eski metodu farklı isimlendir (`SubscribeUntracked`), yeni adı `Subscribe` yap.

### Test
```csharp
[Test]
public void Subscribe_TokenDispose_Unsubscribes()
{
    var prop = new ReactiveProperty<int>(0);
    int calls = 0;
    var token = prop.Subscribe(_ => calls++);
    prop.Value = 1;  // calls = 1
    token.Dispose();
    prop.Value = 2;  // calls still 1
    Assert.AreEqual(1, calls);
}
```

### Dependency
F8 (`SubscriptionToken` type'ı paylaşılabilir).

### Kabul kriteri
`BindingScope.Dispose()` çağrısından sonra hiçbir handler tetiklenmez; `SubscriberCount == 0`.

---

## F10 — unit-16 #1: Source Generator Upper-Bound Check

**Severity:** MEDIUM • **Effort:** 1-2 gün (perf ölçüm dahil) • **Breaking:** Generated kodda perf etkisi olabilir

### Hedef dosya
`SourceGenerationECS~/EntityQueryGenerator.cs:91-130` (özellikle line 127)

### Mevcut kod (generated)
```csharp
unsafe {
    int min = c1, idx = 0;
    if (c2 < min) { min = c2; idx = 1; }
    // ...
    int* ents = idx switch { 0 => set1.GetDenseEntityPtr(), ... };
    for (int i = 0; i < min; i++) {
        int e = ents[i];
        int d1 = set1.GetDenseIndex(e), d2 = set2.GetDenseIndex(e), ...;
        if (d1 < 0 || d2 < 0 || ...) continue;
        action(e, ref *(set1.GetDataPtr() + d1), ref *(set2.GetDataPtr() + d2), ...);
        //                                ↑↑↑ no upper-bound check
    }
}
```

`d{i}` yalnızca negative kontrolü görüyor. Eğer SparseSet internals'ında bug olursa veya bir thread struct mutate ederse, `d{i}` `set{i}.Count`'tan büyük olabilir → out-of-bounds read.

### Fix opsiyon A: Burst safety checks'e güven
Unity Burst `ENABLE_UNITY_COLLECTIONS_CHECKS` aktifken `NativeArray` access bounds check eder. `GetDataPtr()` raw pointer döndüğünden bu check by-pass olur. **Pratikte mevcut design Unity ECS konvansiyonuna uygun** — `unsafe + NativeDisableUnsafePtrRestriction` kabul edilmiş.

### Fix opsiyon B: Conditional bounds check (debug build only)
Generator'a debug-only check ekle:
```csharp
sb.AppendLine("                    int e = ents[i];");
// ... existing d{i} reads ...
sb.AppendLine("#if STRADA_ECS_BOUNDS_CHECK || UNITY_EDITOR");
for (int i = 1; i <= n; i++)
    sb.AppendLine($"                    if (d{i} >= set{i}.Count) throw new IndexOutOfRangeException(\"d{i} out of bounds\");");
sb.AppendLine("#endif");
sb.Append("                    action(e");
// ...
```

Bu, release build'de hot path'i etkilemez, ama editor/debug'da güvenlik sağlar.

### Fix opsiyon C: Storage seviyesinde tutarlılık invariant'ı zorla
`SparseSet.GetDenseIndex(e)` `< 0` döndürmüyorsa garanti olarak `< _count` olduğunu doc + ufak debug assert ile garanti et. Hot path'i değiştirme.

### Önerilen yol
**Opsiyon B + C kombinasyonu:** Editor/debug build'lerinde inject edilen check + `SparseSet.GetDenseIndex` doc invariant'ı.

### Perf ölçüm (gerekli)
Generated query, framework'ün en hot path'i. Eklenen check'in cycle cost'unu ölç:
- Mevcut `Benchmarks/EntityQueryBenchmarks` çalıştır (varsa)
- Editor mode + release mode arasında karşılaştır
- 8-component query'de 1M iteration sonucu sapma %5'ten az olmalı

### Test
```csharp
[Test]
[Conditional("UNITY_EDITOR")]
public void Query_DebugBuild_RejectsOutOfBoundsDenseIndex()
{
    // SparseSet'i mock'la, GetDenseIndex'i set.Count + 1 döndürsün
    // ForEach throw IndexOutOfRangeException etmeli
}
```

### Dependency
Source generator template değişikliği — generator-only repo (`SourceGenerationECS~`) ayrı asset olduğu için Unity asset reimport gerekli.

### Kabul kriteri
Debug/Editor build'de bozuk SparseSet state ile `ForEach` çağrılırsa explicit `IndexOutOfRangeException`; release build'de hot path perf bozulmamalı.

---

## Önerilen Uygulama Sırası

**Sprint 1 (1-2 gün):**
- F1, F3, F4 (toplam ~2.5 saat, sıfır breaking risk)
- F5 (DI-08) — source generator template ile koordineli

**Sprint 2 (2-3 gün):**
- F2 (DI-11) — opsiyonel disposal tracking flag'i ile
- F6 (unit-03 #2) — `[AutoBindingScope]` attribute deprecation cycle başlat
- F7 (MOD-03) — `SerializableType<TBase>` generic + obsolete shim

**Sprint 3 (1 hafta):**
- F8 + F9 birlikte — ortak `SubscriptionToken` + `BindingScope` API'si, major version bump gerekir
- F10 — perf ölçüm + debug-build bounds check

**v4.0 hazırlığı:**
- F6, F8, F9 deprecation period bitince hard removal
- F5 internal yapılırsa public API surface'ı doğru ölçüde küçülür

---

## Son Notlar

- **OPEN-BY-DESIGN bulgular (5 adet)** bu plana dahil değil; onlar için ayrı bir "design decision documentation" PR'ı önerilir — kodda `// FRAMEWORK DESIGN:` yorumları ile kasıtlı oldukları işaretlensin.
- **PARTIAL bulgular (11 adet)** için ayrı bir audit/triage gerekir; bazıları (örn. `World.Current` mutable static) F2-F9 ile çakışan refactor'lar gerektirir.
- Tüm fix'ler için **regression test'ler önerilir** (özellikle F2, F8, F9, F10 — runtime davranışı değişiyor).

**Reviewer:** Claude (2026-05-22)
**Önceki raporlar:** [HIGH status](./2026-05-22-status-review.md) • [MEDIUM status](./2026-05-22-medium-status-review.md)
