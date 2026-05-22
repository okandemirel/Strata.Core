# MEDIUM Severity Status Review — 2026-05-22

**Scope:** 2026-03-07 tarihli 20 birim güvenlik raporundaki tüm **MEDIUM severity** bulgularının mevcut kodda durumu.

**Method:** Her bulgu mevcut kod ile karşılaştırıldı. Subagent çıktıları main agent tarafından spot-check edildi (önceki HIGH review'da hallucination tecrübesi nedeniyle). Birden fazla raporda geçen aynı bulgular dedupe edildi (örn. DI-09 ≡ unit-02 DI-01; MOD-03 ≡ unit-17#1).

**Codebase tarihi:** 2026-03-07 → 2026-05-22 arasında kaynak kodda değişiklik yok. Bulguların büyük çoğunluğu, 2026-02-21 tarihli `cf55a20` ve `ee6ef8f` güvenlik fix commitleri sırasında zaten kapatılmış; rapor metinleri o fix'leri yansıtmıyor.

---

## Toplam Skor

| Status | Count |
|--------|------:|
| **FIXED**             | 37 |
| **PARTIAL**           | 11 |
| **OPEN**              | 11 |
| **OPEN-BY-DESIGN**    |  5 |
| **Total (dedupe)**    | **64** |

Toplam ham MEDIUM bulgu raporlardan 73; 9 tanesi farklı unit'lerde tekrarlandığı için dedupe edildi → 64 unique.

---

## OPEN (gerçek aksiyon gerektiren) — 11

| # | Konu | Dosya | Risk |
|---|------|-------|------|
| 1 | **DI-08** DirectFactory `public static Func<>` field, dışarıdan değiştirilebilir | `Runtime/DI/IStradaFactory.cs:7`, `Container.cs:408` | Global state tampering — bir mod kötü niyetli factory enjekte edebilir |
| 2 | **DI-11** Transient IDisposable hiç dispose edilmiyor (tracking yok) | `Runtime/DI/Container.cs:328-330` | Resource leak — özellikle file handle, native handle vs. |
| 3 | **unit-04 #8** `SparseSet.EnsureSparseCapacity` `_sparse.Length * 3` overflow check yok | `Runtime/ECS/Storage/SparseSet.cs:195` | 1M cap pratikte mitigates; uç durum |
| 4 | **unit-05 #6** EntityCommandBuffer thread-safe değil, doc warning yok | `Runtime/ECS/Jobs/EntityCommandBuffer.cs:20-47` | Job çağıranı yanlış kullanırsa state corruption |
| 5 | **MOD-03** `SerializableType.Type.GetType` resolved type validation yok | `Runtime/Modules/SerializableType.cs:28` | Asset deserialization → arbitrary type instantiation (asset bundle güvenilmezse) |
| 6 | **unit-09 #04** EventBus handler lifecycle-aware unsubscribe yok | `Runtime/Communication/EventBus.cs:159-263` | Caller dispose etmezse memory leak |
| 7 | **unit-11 SYNC-02** ReactiveProperty/Collection subscription'lar BindingScope olmadan leak ediyor | `Runtime/Sync/ReactiveProperty.cs` | Aynı pattern |
| 8 | **unit-03 Finding 2** AutoBinding default include patterns `"Game.*"` permissive | `Runtime/DI/AutoBinding/RuntimeAutoBindingScanner.cs:19` | `Game.MaliciousMod.dll` otomatik scan edilir; pattern bir güvenlik sınırı değil |
| 9 | **unit-12 #3** Namespace injection in generated C# code | `Editor/ModuleGenerator/Pipeline/Steps/FileGenerationStep.cs:155-502` | `}`, `;` gibi karakterler namespace bloğundan kaçabilir |
| 10 | **unit-12 #5** `SetTargetPath` validation bypass | `Editor/ModuleGenerator/StradaModuleGenerator.cs:88-92` | Programatik path injection (UI validation atlanır) |
| 11 | **unit-16 #1** Generated EntityQuery unsafe pointer arithmetic — upper bound check yok | `SourceGenerationECS~/EntityQueryGenerator.cs:127` | `ref *(set.GetDataPtr() + d)` üst sınır kontrolü olmadan; sadece negatif index reddediliyor |

---

## PARTIAL (kısmi mitigasyon, audit önerilir) — 11

| # | Konu | Dosya | Eksik kalan |
|---|------|-------|-------------|
| 1 | **DI-05** Auto-binding empty catch | `Runtime/DI/ContainerBuilderExtensions.cs:69-83` | `TypeLoadException`/`ReflectionTypeLoadException` artık logged, ama generic `Exception` hâlâ swallow ediliyor |
| 2 | **DI-10 / unit-19 #4** LifecycleProcessor cache | `Runtime/DI/LifecycleProcessor.cs:10-11, 57-86` | Dictionary lock altında ama `ConcurrentDictionary`'e tam swap yapılmadı |
| 3 | **unit-02 DI-04** Lifecycle method exception handling | `Runtime/DI/LifecycleProcessor.cs:24-32, 45-53` | DeConstruct try/catch'li, ama PostConstruct hâlâ propagate edebiliyor |
| 4 | **unit-04 #9** SparseSet raw pointer methods public | `SparseSet.cs:117-121` | `GetUnsafePtr/GetDataPtr` public; NativeSlice alternatifleri eklendi ama eski API kalktırılmadı |
| 5 | **unit-04 #12** `EntityManager.RestoreState` validation | `EntityManager.cs:316-340` | Bounds check var ama active/version semantik tutarlılığı kontrol edilmiyor |
| 6 | **unit-06 #4** `World.Current` mutable static | `Runtime/ECS/World/World.cs:9, 21-25` | Setter `internal` yapıldı, alan `volatile`; ama hâlâ değiştirilebilir global |
| 7 | **unit-07 #3 / unit-20 #1** Stack trace disclosure | `Runtime/Bootstrap/GameBootstrapper.cs:340-357` | Unity `Debug.LogError` build-gated (`{ex.Message}` prod), ancak `StradaLog.LogError($"...\n{ex.StackTrace}")` **her build'de** stack trace logluyor (lines 344, 357) |
| 8 | **unit-07 #5** Async module init race | `Runtime/Bootstrap/GameBootstrapper.cs:208-231` | Phased init mevcut ama static `Container/Services` getter'ları async coroutine süresince erişilebilir |
| 9 | **unit-14 #1** Inspector dynamic instantiation | `Editor/Windows/StradaEntityInspectorWindow.cs:855` | Type cached ve filter edildi ama `Activator.CreateInstance` öncesi yeniden doğrulanmıyor |
| 10 | **unit-20 #2** Container disposal log exposes exception | `Runtime/DI/Container.cs:196-200` | `Debug.LogError` build-gated (`{e.Message}` prod), ama `StradaLog.LogError($"...: {e}")` her zaman full exception object'i interpolate ediyor |
| 11 | **unit-20 #4** Empty catch blocks (rapor 13 örnek) | `Runtime/DI/ContainerBuilderExtensions.cs:79-82` | Runtime kodundaki örnek log'a dönüştürüldü; editor kodundaki diğer örnekler ayrı audit gerektirir |

---

## OPEN-BY-DESIGN (kabul edilen tasarım kararı) — 5

| # | Konu | Neden kabul edildi |
|---|------|--------------------|
| 1 | **DI-09 / unit-02 DI-01** `BindingFlags.NonPublic` ile private field/method injection | DI framework'leri için standart pattern; `[Inject]` attribute opt-in. Public-only zorlamak framework ergonomisini kırar. |
| 2 | **unit-05 #3** `[NativeDisableUnsafePtrRestriction]` parallel job pointers | Unity Burst/Jobs konvansiyonu; Burst safety check'ler debug build'de zaten devrede |
| 3 | **unit-06 #5** Reactive operations thread-safety yok | Unity main-thread modeline göre tasarlandı; lock eklemek hot path perf'i bozar |
| 4 | **unit-11 POOL-01** ObjectPool state reset callback'e bırakılmış | Pool semantiği kullanıcı tarafından belirlenir (oyun nesnesine göre değişir); zorla reset yanlış olabilir |
| 5 | **unit-11 FSM-02** OnStateChanged içinde reentrancy guard yok | `SetState` zaten transition chain (`CheckTransitions`) tarafından recursive olarak çağrılıyor (line 113, 124); reentrancy intentional |

---

## Düzeltilmiş Subagent Yanlışları (transparency)

4 paralel subagent kullanıldı (Batch 1-4). Sonuçlar spot-check edildi. Bulunan hatalar:

| Bulgu | Agent verdict | Doğrulanmış verdict | Sebep |
|-------|---------------|---------------------|-------|
| **DI-04** | OPEN | FIXED-BY-DESIGN | `Type.GetType` çağrılarının hepsi **hardcoded** assembly-qualified string ile (`"Strada.Generated.StradaGeneratedRegistry, Assembly-CSharp"`); "untrusted string" senaryosu yok |
| **unit-15 #1** | OPEN | FIXED | `BenchmarkPersistence.ValidatePath` (line 22-27) `Path.GetFullPath` + `StartsWith(projectRoot)` ile proper canonicalization yapıyor |
| **unit-15 #3** | OPEN | FIXED | `DeleteSession` (line 137-145) `ValidatePath(path)` çağrısıyla başlıyor; `File.Delete` sadece scope içindeyse çalışıyor |
| **unit-10 #3** | INVALID-FILE-NOT-FOUND | FIXED | `Runtime/Patterns/Controller.cs` mevcut; `InjectModel` (line 21) `if (Model != null) return;` idempotency guard'ına sahip |
| **unit-20 #2** | OPEN | PARTIAL | Unity `Debug.LogError` build-gated; ancak `StradaLog.LogError` her zaman full `{e}` interpolate ediyor — kısmi mitigasyon |
| **unit-07 #3 / unit-20 #1** | OPEN | PARTIAL | Aynı pattern — Debug.LogError prod'da `.Message` kullanıyor, StradaLog her zaman `.StackTrace` ekliyor |
| **unit-11 FSM-02** | FIXED+notes | OPEN-BY-DESIGN | Reentrancy `CheckTransitions` tarafından kasıtlı kullanılıyor; "fix" olarak ele alınmamalı |

---

## Modül Bazında Yoğunluk

| Modül | OPEN | PARTIAL | Toplam aksiyon |
|-------|-----:|--------:|---------------:|
| **DI Core (Container/Builder)** | 2 | 3 | 5 |
| **DI AutoBinding/Reflection** | 1 | 0 | 1 |
| **ECS Storage/Core** | 1 | 2 | 3 |
| **ECS Jobs/Buffer** | 1 | 0 | 1 |
| **ECS Reactive/World** | 0 | 1 | 1 |
| **Modules/Bootstrap** | 1 | 2 | 3 |
| **Communication (EventBus)** | 1 | 0 | 1 |
| **Sync (ReactiveProperty)** | 1 | 0 | 1 |
| **Editor ModuleGenerator** | 2 | 1 | 3 |
| **Editor Inspector/Bench** | 0 | 1 | 1 |
| **Source Generator** | 1 | 0 | 1 |
| **Error Handling (cross-cut)** | 0 | 3 | 3 |

**En yoğun aksiyon alanları:** DI Core (5) ve Editor ModuleGenerator (3).

---

## Önerilen Aksiyon Sırası

1. **Quick wins (1-2 saat her biri):**
   - **DI-11**: `Container.cs:328` Transient lifetime için `IDisposable` instance'larını da `_disposalStack`'e push et (HIGH-impact, basit fix)
   - **unit-04 #8**: `SparseSet.cs:195` capacity büyütmede `checked` arithmetic veya `Math.Min(newCapacity, MaxSparseCapacity)` öncesi overflow guard
   - **unit-12 #5**: `StradaModuleGenerator.SetTargetPath` içinde `ValidateTargetPath` çağrısı

2. **Medium (yarım gün):**
   - **DI-08**: `DirectFactory<T>.Delegate` public set'i private yapıp `Register()` static method'u ile expose et
   - **MOD-03 / unit-17 #1**: `SerializableType.Type` getter'a allowlist (örn. `ISystem`, `IModuleInstaller` gibi temel arayüzlere kısıtla)
   - **unit-12 #3**: Namespace string'inde regex check (`^[A-Za-z_][A-Za-z0-9_.]*$`) zorla
   - **unit-07 #3 / #20 #1**: `StradaLog.LogError` çağrılarında `#if !UNITY_EDITOR && !DEVELOPMENT_BUILD` koşullu olarak stack trace'i çıkar
   - **unit-20 #4**: Editor klasöründeki kalan empty catch bloklarını audit et + log ekle

3. **Tasarım kararı gerektiren (gün+):**
   - **unit-09 #04 / unit-11 SYNC-02**: BindingScope / IDisposable subscription token pattern'i tüm reactive subscription API'lerine ekle (breaking API)
   - **unit-03 #2**: AutoBinding default include pattern'inden `"Game.*"` çıkar; opt-in `[Strada]` attribute ile değiştir
   - **DI-09 (OPEN-BY-DESIGN)**: `[Inject]` attribute'a `AllowPrivate = false` default ekle; private opt-in olsun

4. **Üst seviye:**
   - **unit-16 #1**: Source generator'da generated query kodunda upper-bound check inject et (perf'i etkiler — ölç + Burst safety check'lere bırak)
   - **unit-14 #1**: Editor inspector'da `Activator.CreateInstance` öncesi cached type'ı yeniden valid et

---

**Reviewer:** Claude (interactive verification + 4 parallel subagents + main-agent spot-checks, 2026-05-22)

**İlgili rapor:** [`2026-05-22-status-review.md`](./2026-05-22-status-review.md) — HIGH severity bulgu durumu.
