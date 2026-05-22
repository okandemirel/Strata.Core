# Security Review Status — 2026-05-22

**Scope:** Konsolide durum incelemesi — 2026-03-07'de yazılan 20 birim güvenlik raporundaki **HIGH severity** bulgularının mevcut kodda durumu.

**Methodology:** Raporlardaki dosya:satır referansları mevcut kod ile karşılaştırıldı. Her bulgu OPEN / FIXED / PARTIAL / OPEN-BY-DESIGN olarak işaretlendi.

**Codebase delta:** 2026-03-07 → 2026-05-22 arasında kaynak kodda değişiklik **yok** (yalnızca .meta, FUNDING.yml, README localizations). Yani raporlardaki tüm bulgular hâlâ mevcut codebase'i temsil ediyor. Ancak 2026-02-21'de yapılan kritik güvenlik commitleri (`cf55a20`, `ee6ef8f`) sırasında pek çok HIGH bulgu zaten **fix edilmiş**; rapor metni o fix'leri yansıtmıyor.

---

## Severity Totals (20 birim raporu)

| Severity | Count |
|----------|-------|
| HIGH     | 15    |
| MEDIUM   | 73    |
| LOW      | 90    |
| INFO     | 27    |
| **Total**| **~205** |

CRITICAL hiçbir raporda yok.

---

## HIGH Findings Status

### FIXED (13/15)

| # | ID / Konu | Rapor | Mevcut kod kanıtı |
|---|-----------|-------|-------------------|
| 1 | **DI-01** Singleton resolution race (double-instantiate) | unit-01 | `Container.cs:299-321` — singleton factory artık `Volatile.Read(_singletons[index])` + `Interlocked.CompareExchange` paterniyle; CAS kaybeden thread oluşturduğu instance'ı `IDisposable.Dispose()` çağırarak temizliyor. |
| 2 | **DI-02** Use-after-dispose race in `Container.Dispose` | unit-01 | `Container.cs:44` → `private volatile bool _disposed`. `Dispose()` (178-210) lock acquire + double-check pattern, disposal stack lock altında işleniyor. |
| 3 | **DI-03** `ContainerScope.Dispose` thread-safety | unit-01 | `ContainerScope.cs:17` volatile `_disposed`, `Dispose()` (146-161) double-check pattern + `Volatile.Read(ref _scopedInstances[i])` her bir instance için. |
| 4 | **DI-03** LifecycleProcessor cache TOCTOU | unit-02 | `LifecycleProcessor.cs:57-86` — `GetOrCacheMethods` artık ilk satırda `lock (_lock)` açıyor; raporda eleştirilen lock dışı `TryGetValue` paterni kaldırılmış. |
| 5 | Auto-binding cache ignores filter params | unit-03 | `RuntimeAutoBindingScanner.cs:47-60` — fast-path artık `MatchesCachedPatterns(cached, includePatterns, excludePatterns)` ile filter parametrelerini de eşliyor; DCL paterni kullanılıyor. |
| 6 | `SparseSet.Get` missing bounds check | unit-04 | `SparseSet.cs:75-79` — explicit `if (entityIndex >= _sparse.Length \|\| _sparse[entityIndex] < 0) throw InvalidOperationException`. |
| 7 | `SparseSet.GetRef` unsafe pointer deref | unit-04 | `SparseSet.cs:82-92` — hem entityIndex hem denseIndex bounds check'leri var, raw pointer cast öncesi. |
| 8 | `EntityManager.GetComponent` missing entity validation | unit-04 | `EntityManager.cs:181-187` — `if (!Exists(entity)) ThrowEntityNotExists(entity)` (version validation dahil). `GetComponentRef` da aynı pattern. |
| 9 | `CommandReader` lacks bounds checking | unit-05 | `EntityCommandBuffer.cs:250-307` — `ReadCommand/ReadByte/ReadInt/ReadULong/ReadBytes` hepsi `Remaining` veya `_position + count > _data.Length` kontrolü yapıyor; `InvalidOperationException` fırlatıyor. |
| 10 | Deferred entity index unbounded | unit-05 | `EntityCommandBuffer.cs:233-235` — `if (index < 0 \|\| index >= _createdEntities.Length) throw IndexOutOfRangeException`. |
| 11 | **FIND-09-01** EventBus dispatch/registration race | unit-09 | `EventBus.cs:104,126,146` dispatch path'leri `Volatile.Read(ref _signalHandlers/_queryHandlers/_eventChannels)`; kayıt path'leri (163-229) `lock(_lock)` + `Volatile.Write`. ARM-zayıf-memory için doğru pattern. |
| 12 | ComponentPlayback static `Dictionary` not thread-safe | unit-19 | `EntityCommandBuffer.cs:330` — `private static readonly ConcurrentDictionary<ulong, IComponentPlaybackHandler> _handlers = new();` Dictionary → ConcurrentDictionary swap yapılmış. |
| 13 | Multiple Bootstrapper instances (no singleton enforcement) | unit-07 #2 | `GameBootstrapper.cs:105-110` — `if (_instance != null && _instance != this) { Destroy(this); return; } _instance = this;` Unity singleton pattern uygulanmış. |

### PARTIAL (1/15)

| # | ID / Konu | Rapor | Durum |
|---|-----------|-------|-------|
| 14 | Incomplete resource cleanup on partial init failure | unit-07 #4 | `GameBootstrapper.cs:149-360` — `InitializeAsync` coroutine artık `TryExecute` pattern kullanıyor (line 201), module init try/catch içinde (line 287-305), `DisposeResources()` (line 356) ve `_isInitialized = false` (line 359) ile rollback var. **Ancak:** kısmi init başarısızlığında container/world/systemRunner'ın hangi sırayla dispose edildiği ve `_initializedModuleConfigs` listesinin temizlenip temizlenmediği tam audit edilmedi — derinlemesine review önerilir. |

### OPEN — BY DESIGN (1/15)

| # | ID / Konu | Rapor | Durum |
|---|-----------|-------|-------|
| 15 | Mutable global static state — Container, Services, World, Systems | unit-07 #1 | **STILL OPEN**, ancak design olarak kabul edilmiş. `GameBootstrapper.cs:55,65,76,86` halen `public static IContainer Container/Services/World/Systems { get; private set; }` tutuyor. Kodda explicit yorum: *"WARNING: This static reference is NOT thread-safe. The framework currently supports only a single World instance at a time."* Bu, multi-World senaryosu desteklenmediği sürece kabul edilebilir bir tasarım kısıtı; ancak production'da iki Bootstrapper sahnesi yan yana geldiğinde sessiz davranış sorunları doğurabilir. **Öneri:** ya runtime'da ikinci bootstrap denemesinde sert hata fırlat, ya da statik getter'ları `internal` yapıp public API'yi instance üzerinden expose et. |

---

## Özet Skoru

```
FIXED:         13/15  (87%)
PARTIAL:        1/15  ( 7%)
OPEN (design):  1/15  ( 7%)
TRULY OPEN:     0/15
```

**Sonuç:** 2026-02-21 commit'lerinde HIGH bulguların büyük çoğunluğu zaten kapatılmış. Rapor metinleri o fix'leri yansıtmadığı için "açık görünüyor" ama kodda kapalılar. Geriye 1 partial (cleanup ordering audit) + 1 design-accepted (statik bootstrap globals) kaldı.

---

## Sonraki Adım Önerileri (öncelik sırasıyla)

1. **MEDIUM bulgu sürüsü (~73 adet)** — şu an review edilmedi. En verimli kazanç burada:
   - `TypeRegistry.GetId` TOCTOU (unit-19 Finding 1) → `ConcurrentDictionary` swap
   - `InjectionProcessor.GetOrCacheInfo` TOCTOU (unit-19 Finding 2) → aynı
   - `World.Current` global mutable (unit-06) — design review
   - `ReactiveWorld` callback exception breaks chain (unit-06)
   - `EventBus` 3 MEDIUM finding (unit-09)
2. **unit-07 Finding 4 PARTIAL** — `GameBootstrapper.InitializeAsync` failure path'ini explicit audit; Container/World/SystemRunner dispose sırası deterministik mi?
3. **Static global bootstrap kontratını kabul/reddet** — multi-World desteği roadmap'te yoksa, ikinci bootstrap denemesinde hard-fail ekle; varsa statik field'ları kaldır.
4. **Auto-doğrulama testleri** — fix edilen 13 HIGH için regression test ekleyip, ileride bu bulgular yeniden açılırsa CI'de patlasın (özellikle `volatile`, `ConcurrentDictionary`, bounds-check gibi kolayca geri alınabilen patternler için).
5. **Shannon vb. dynamic pentesterlar bu kütüphane için uygun değil** — Strada.Core HTTP/REST sunucu içermediği için canlı exploit araçlarının saldırı yüzeyi yok. Statik C# review + Roslyn analyzer + Burst safety checks daha verimli.

---

## Verification Notları

- 13 FIXED finding'in her biri için mevcut kodun ilgili satırları okundu ve fix pattern'i doğrulandı.
- Bu raporun ilk taslağında bir Explore subagent kullanıldı; agent ResourceManager.cs, BufferPool.cs, ModuleLoader.cs, JobScheduler.cs, AppBootstrapper.cs gibi **var olmayan dosyalardan** bulgu uydurdu. Bu raporda yalnızca mevcut kodda elle doğrulanan finding'ler bulunmaktadır. Subagent çıktısına güvenilmemiştir.

**Reviewer:** Claude (interactive verification, 2026-05-22)
