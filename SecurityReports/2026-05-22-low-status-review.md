# LOW Severity Status Review — 2026-05-22

**Scope:** 2026-03-07 tarihli 20 birim raporundaki tüm **LOW severity** bulguların mevcut kodda durumu.

**Method:** 5 paralel Explore subagent (DI, ECS Storage/Jobs, Comm+Patterns, Sync+Pool+State+Data, Editor+Bootstrap+SourceGen) + main-agent spot-check düzeltmeleri. Sprint 1+2 fix commit `549d8ff` durumu hesaba katıldı.

**Dedup notu:** 90 ham LOW kayıttan 9 dedupe edildi (MOD-07≡unit-07#10, MOD-08≡unit-19#8, unit-10#9≡unit-19#7, unit-18#2≡unit-05#1 zaten HIGH'da kapalı, vs.) → **~81 unique LOW**.

---

## Toplam Skor (düzeltilmiş)

| Status | Count |
|--------|------:|
| **FIXED**             | 13 |
| **PARTIAL**           | 13 |
| **OPEN**              | 41 |
| **OPEN-BY-DESIGN**    | 14 |
| **Total (unique)**    | **81** |

LOW bulgular genelde defense-in-depth, kod kalitesi veya kabul edilebilir tasarım kararları — **acil aksiyon gerektirmiyor**, ama bir kısmı düşük effort/yüksek değer quick-win'ler.

---

## Modül Bazında Yoğunluk (OPEN + PARTIAL)

| Modül | OPEN | PARTIAL | OPEN-BY-DESIGN |
|-------|-----:|--------:|---------------:|
| DI Core + AutoBinding | 9 | 4 | 4 |
| ECS Storage/Jobs/Query | 2 | 2 | 5 |
| Communication + Reactive | 5 | 2 | 0 |
| Patterns + Sync + State | 9 | 1 | 2 |
| Bootstrap + Modules | 5 | 1 | 2 |
| Editor Tools + Inspectors | 6 | 1 | 0 |
| Editor CodeGen + SourceGen | 5 | 2 | 1 |

---

## Quick Win OPENs (önerilen ilk hedefler)

Düşük effort, somut güvenlik/kararlılık değeri:

| # | Bulgu | Dosya | Tahmini effort |
|---|-------|-------|---------------:|
| Q1 | **unit-07 #7** Verbose logging default `true` | `Runtime/Bootstrap/GameBootstrapperConfig.cs:24` | 5 dk |
| Q2 | **unit-10 #4** TimerService ID int overflow | `Runtime/Services/TimerService.cs:39` | 15 dk |
| Q3 | **unit-11 SYNC-08** EntityHandleRegistry ID overflow | `Runtime/Sync/EntityHandleRegistry.cs:10, 18` | 15 dk |
| Q4 | **unit-09 #05** EventBus type-id counter overflow | `Runtime/Communication/EventBus.cs:363-378` | 20 dk |
| Q5 | **unit-10 #7** `View.UpdateView` null check | `Runtime/Patterns/View.cs:82-86` | 10 dk |
| Q6 | **unit-11 DATA-02** `ConfigData<T>.Data` null setter guard | `Runtime/Data/ConfigData.cs:46-47` | 10 dk |
| Q7 | **unit-10 #5** PatternManager duplicate registration guards | `Runtime/Patterns/PatternManager.cs:37-69` | 30 dk |
| Q8 | **unit-11 SYNC-04** TwoWayBinding `_updating` flag try/finally | `Runtime/Sync/BindingScope.cs:142-188` | 15 dk |
| Q9 | **unit-06 #8** ECSBuilder duplicate system registration | `Runtime/ECS/World/ECSBuilder.cs:26-36` | 15 dk |
| Q10 | **unit-11 SYNC-06** MediatorPool no size cap | `Runtime/Sync/MediatorRegistry.cs:89-119` | 30 dk |

Toplam ≈ 3 saat ile 10 quick win.

---

## Geri Kalan OPEN'lar (31 adet)

### DI Core (sub-batch 1)
- **DI-12** No circular dependency protection for factory/instance registrations (ContainerBuilder.cs:96-97) — runtime crash riski; ek graph traversal gerekir
- **DI-05 (unit-02)** Unbounded reflection cache growth (InjectionProcessor.cs:12) — ConcurrentDictionary'de eviction yok; uzun-süreli prosesler için minor
- **DI-07 (unit-02)** Lifecycle attributes execution order non-deterministic (LifecycleProcessor.cs:44-56) — `[PostConstruct(Order = N)]` parametresi eklenebilir
- **DI-08 (unit-02)** `AutoRegisterAttribute.As` compile-time constraint yok (AutoRegisterAttribute.cs:9) — Roslyn analyzer ile karşılanabilir
- **DI-13 / unit-03 #2 / unit-17 #7** AutoBinding patterns hâlâ permissive — Sprint 2 [AutoBindingScope] deprecation cycle başlattı, sonraki major'da hard error
- **unit-03 #6** Assembly scanning timeout yok (RuntimeAutoBindingScanner.cs:51) — büyük projelerde frame spike

### Communication + Reactive + Patterns
- **FIND-09-06** `EventBus.SendAsync` handler check semantiği belirsiz (EventBus.cs:296-308) — agent bunu OPEN dedi ama incelendiğinde mevcut handler validation var; doc gap olabilir
- **FIND-09-07** `SignalSequence.Include` recursion depth tracking yok — nested include'larda derinlik limiti gerekir
- **unit-06 #10** SystemScheduler.AddSystem init sonrası eklemeleri kabul ediyor (SystemScheduler.cs:22-26)
- **unit-10 #6** PatternManager LINQ linear scan (`OfType<T>().FirstOrDefault()`) — perf concern, Dictionary cache çözüyor
- **unit-10 #10** `Base.Dispose` OnDispose çağrı sırası — events unsubscribe before OnDispose tercih edilir

### Sync + Pool + State + Data
- **SYNC-05** EntityView dirty-skip semantik fragile (EntityView.cs:92-101) — explicit doc gerekir
- **SYNC-07** MediatorRegistry.ReleaseAll Dispose vs Unbind belirsizliği — Dispose semantiği netleştirilmeli
- **FSM-03** No state/transition removal mechanism (StateMachine.cs) — dinamik FSM gerekirse API genişletilir; çoğu kullanım için OK

### Bootstrap + Modules
- **unit-07 #6** OnInitializationFailed event subscribers unvalidated — handler exception isolation
- **unit-07 #9** Missing null guard on GetDebugInfo
- **unit-07 #10 / MOD-07** ServiceLocator.TryGet silent exception swallow
- **MOD-08 / unit-19 #8** RuntimeSystemDiscovery static cache unsynchronized — Unity main-thread modelinde düşük risk

### Editor Tools + CodeGen + SourceGen
- **unit-14 #4** Broad exception swallowing in StradaEntityInspectorWindow:650-653
- **unit-14 #6** Bus Debugger regex without timeout (`BusDebuggerWindow.cs:810-821`) — ReDoS riski, editor-only ama eklenmesi ucuz
- **unit-15 #2** DateTime.Parse without invariant culture (`BenchmarkPersistence.cs`)
- **unit-15 #6** JSON deserialization without schema validation (editor-only, low risk)
- **unit-15 #9** Regex from user pattern in BusDataProvider — aynı ReDoS pattern
- **unit-16 #2/#3** Source generator type-name interpolation without escape — generated kod compile-time check'i çözüyor ama yine de sanitize edilmeli
- **unit-16 #4** `#pragma warning disable CS8603/CS8604` in generated code — nullable warning suppression
- **unit-16 #5** Factory class name collision possibility — unique suffix önerilir

### Error Handling
- **unit-20 #8** ComponentBinding._lastError exception message exposure — `LastError` property prod'da `ex.Message` döner; sensitive bilgi sızabilir

---

## OPEN-BY-DESIGN (14 adet — kabul)

Bilinçli tasarım kararları; kodda yorum/dokümantasyon ile açıkça belirtilmiş ya da framework konvansiyonu:

- **DI-09 / unit-02 DI-01 / unit-17 #3** — Reflection-based private member injection (DI konvansiyonu)
- **unit-17 #4** — Expression.Compile cached per-factory; design accepted
- **unit-04 #11** — ArchetypeManager entity list growth (Unity ECS pattern)
- **unit-04 #13** — EntityManager/SparseSet single-threaded design (Unity main-thread)
- **unit-05 #5 / unit-18 #5** — `[NativeDisableUnsafePtrRestriction]` parallel jobs için (Burst requirement)
- **unit-11 SYNC-09** — ReactiveProperty no thread safety (single-thread doc)
- **unit-12 #4** — `allowUnsafeCode: true` asmdef default (framework requires unsafe blocks)
- **unit-13 #1** — Generated code uses Type.FullName (Roslyn syntax check downstream)
- **unit-14 #5 / unit-15 #5** — Extensive reflection for editor debugging (editor-only)
- **unit-20 #5** — Container disposal `catch (Exception)` broad (resource cleanup must continue)

---

## FIXED (13 adet — ipucu)

Sprint 1+2 ve önceki güvenlik commitleri ile zaten kapatılmış:

- unit-04 #10 ComponentStorage logged exceptions
- unit-04 #14 SparseSet.Add bounds (HIGH'da FIXED, dup)
- unit-05 #9 EntityCommandBuffer double-dispose guard (`_isCreated`)
- unit-05 #10 / unit-18 #2 ComponentPlayback `ConcurrentDictionary` (HIGH'da FIXED, dup)
- unit-06 #1/#2/#3 Reactive notification (snapshot + depth guard) — MEDIUM'da FIXED
- unit-06 #7 Callback list snapshot
- unit-11 SYNC-04 TwoWayBinding reentrancy (agent FIXED dedi, batch 4 doğruladı)
- unit-11 POOL-02 ActiveCount calculation
- unit-11 POOL-03 Double-despawn HashSet guard
- unit-11 FSM-04 Transition condition exceptions (CheckTransitions caller safe)
- unit-11 DATA-01 ConfigData GUID lazy + serialized
- unit-13 #3 SystemRegistryGenerator catch + LogWarning (subagent yanlış OPEN dedi, spot-check düzeltti)
- unit-19 #12 EventBus.Dispose flag (HIGH'da FIXED, dup)

---

## Düzeltilmiş Subagent Yanlışları (transparency)

| Bulgu | Agent verdict | Doğrulanmış verdict | Sebep |
|-------|---------------|---------------------|-------|
| **unit-13 #3** | OPEN | **FIXED** | `SystemRegistryGenerator.cs:94-96` `catch (Exception ex) { Debug.LogWarning(...) }` — sessiz değil, loglu |
| **LOW-007/023/035** (unit-12 #4 dup) | 3 ayrı OPEN | **1 OPEN-BY-DESIGN** | Aynı `allowUnsafeCode: true` bulgusu — framework requirement |
| **MOD-04** (LOW-019) | OPEN | **PARTIAL** | ModuleRegistry.cs:28-47 prefix filter (`Strada.` / `Game.` / `Assembly-CSharp`) var; sıkı allowlist değil ama scan kapsamı sınırlı. AutoBinding ile aynı pattern; aynı sertleştirme yolu uygulanabilir |
| **LOW-018, 034, 037** | OPEN (dup) | **Kaldırıldı** | Generic "type loading without validation" tekrarları; MOD-03 (MEDIUM) ile dedupe |
| Batch 5 bias | 0 FIXED | birkaç FIXED | Agent editor-only ve framework-requirement bulgularını sert OPEN olarak işaretledi; düzeltildi |

---

## Karşılaştırma — Severity Seviyeleri

| Seviye | Total | FIXED | PARTIAL | OPEN | OBD |
|--------|------:|------:|--------:|-----:|----:|
| HIGH (15) | 15 | 13 | 1 | 0 | 1 |
| MEDIUM (64) | 64 | 44* | 11 | 4 | 5 |
| LOW (81) | 81 | 13 | 13 | 41 | 14 |

*MEDIUM FIXED sayısı Sprint 1+2 sonrası 37 → 44'e çıktı (7 ek fix Sprint commit'inde).

LOW seviyesinde OPEN oranı yüksek (≈51%) çünkü LOW'lar tipik olarak:
- Kod kalitesi / doc gap'leri
- Defense-in-depth opsiyonları
- Kabul edilebilir-design design accepts
- Quick-win'ler

Acil iş gerektirmiyor — Sprint planlamasında Q1-Q10 quick-win'leri ile başlanması önerilir.

---

## Önerilen Yaklaşım

1. **Sprint 3 (alternatif scope):** Sadece Q1-Q10 quick-win'leri uygula (≈3 saat). 10 OPEN LOW kapanır, kalan 31 OPEN için triage kararı verilir.
2. **Sprint 4:** Editor tooling hardening — regex timeout, JSON schema, codegen sanitization batch'i.
3. **Sprint 5:** Source generator hardening (unit-16) — type name escape + class name collision guard.
4. **Backlog'a:** OPEN-BY-DESIGN bulgular için kodda `// FRAMEWORK DESIGN:` yorumları ekleyerek tasarımın kasıtlı olduğunu işaretle.

**Reviewer:** Claude (5 paralel Explore subagent + main agent spot-checks, 2026-05-22)

**İlgili raporlar:**
- [HIGH status](./2026-05-22-status-review.md)
- [MEDIUM status](./2026-05-22-medium-status-review.md)
- [MEDIUM fix plans](./2026-05-22-medium-fix-plans.md)
