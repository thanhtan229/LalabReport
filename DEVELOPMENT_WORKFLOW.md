# DEVELOPMENT_WORKFLOW.md

> **Tinix Development, Validation, Reload, Restart & Packaging Standard**
>
> This document defines how a Tinix project should be developed, validated, reloaded, restarted, health-checked, and prepared for user testing.
>
> It is intentionally **framework-agnostic, runtime-agnostic, and platform-aware**. Project-specific commands belong in the **Project Runtime Profile** section of this file.

---

## 0. Purpose

The development workflow should optimize for:

- fast iteration;
- reliable verification;
- minimal disruption;
- clear runtime state;
- safe process handling;
- easy user testing;
- avoiding unnecessary builds and packaging.

The default development loop is:

`Understand → Change → Validate → Build when justified → Apply latest code → Verify runtime → Ready for user test`

The application should be left in a state where the latest relevant changes can be tested immediately.

---

## 1. Scope and Rule Strength

This file governs:

- development mode;
- validation;
- build decisions;
- frontend reload;
- backend/service restart;
- full application restart;
- process ownership;
- runtime health checks;
- development scripts;
- packaging behavior;
- release-related escalation.

Rule strength:

- **MUST / MUST NOT** — required unless a documented project-specific rule overrides it.
- **SHOULD / SHOULD NOT** — default behavior; deviate only for a concrete reason.
- **MAY** — optional based on the project.

This file does **not** define:

- product requirements;
- UI design language;
- architecture;
- coding style.

Use the appropriate project documents for those concerns.

---

## 2. Project Runtime Profile

Every non-trivial project SHOULD complete this section.

Do not invent commands. Use the commands and runtime model that actually exist in the repository.

```text
Product name: Lalab Auto Report

Project type:
- Desktop app

Primary platform: Windows (Windows 10 / Windows 11 x64)

Frontend runtime: WPF (.NET 10.0-windows, XAML, CommunityToolkit.Mvvm)
Backend runtime: In-process (.NET 10 LTS: LalabAutoReport.Core, LalabAutoReport.Infrastructure)
Desktop/native runtime: WPF (.NET 10.0-windows)
Database/runtime dependency: SQLite (WAL mode, Dapper, Microsoft.Data.Sqlite)

Development start command: DEV_START.bat
Development stop command: DEV_STOP.bat
Development restart command: RESTART.bat (or DEV_RESTART.bat)

Frontend dev command: N/A (Compiled into desktop application)
Backend dev command: N/A (Compiled into desktop application)

Targeted test command: dotnet test --filter <TestName>
Typecheck command: dotnet build -c Debug
Lint command: dotnet build -c Debug
Build command: dotnet build src\LalabAutoReport.UI\LalabAutoReport.UI.csproj -c Debug
Full test command: TEST.bat (or dotnet test --nologo)

Health-check command / endpoint: DEV_STATUS.bat (or powershell -Command "Get-Process -Name 'LalabAutoReport.UI'")

Package command: PACKAGE.bat (or dotnet publish src/LalabAutoReport.UI/LalabAutoReport.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true)
Release command: PACKAGE.bat

HMR / live reload:
- Not supported (WPF desktop binary; fast restart via RESTART.bat is primary dev loop)

Manual UI reload:
- Runtime-specific command: RESTART.bat (fast restart loop ~1-2s)
```

If a field is not applicable, mark it `N/A`.

---

## 3. Development Mode Is the Default

Unless the task explicitly concerns release, distribution, installer behavior, or production packaging, assume:

`Development / Test Mode`

This means:

- prefer running from source;
- prefer development runtime;
- prefer targeted validation;
- use the least disruptive reload/restart mechanism;
- do not package distribution artifacts by default;
- leave the application ready for testing.

Do not turn routine development into a release pipeline.

---

## 4. Build and Package Are Different

A **build** validates or prepares executable application code.

A **package** creates a distributable artifact.

### 4.1. Build

A build may detect:

- compile errors;
- type errors;
- bundling errors;
- module/import errors;
- dependency incompatibilities;
- configuration problems;
- production-only build failures;
- frontend/backend integration issues.

A build does not necessarily create an installer or executable distribution.

### 4.2. Package

Packaging may create artifacts such as:

- `.exe`;
- `.msi`;
- `.dmg`;
- `.app`;
- `.apk`;
- `.ipa`;
- installer;
- portable build;
- release archive;
- production distribution bundle;
- container image;
- deployable package.

Do not treat `build` and `package` as synonyms.

---

## 5. Do Not Package After Every Coding Task

Routine development MUST NOT automatically trigger packaging.

Do not automatically:

- create installers;
- build release archives;
- create production executables;
- run release pipelines;
- rebuild distributable packages merely to test a normal code change.

Package only when:

- the user explicitly requests it;
- the task concerns packaging or distribution;
- packaged-runtime behavior must be tested;
- installer behavior must be tested;
- auto-update or release behavior changed;
- a release/milestone requires it;
- a documented technical requirement makes packaging necessary.

Otherwise, test from source or the normal development build.

---

## 6. Decide Whether a Build Is Needed

A full build is **not required after every small change**.

Use the smallest validation level that gives sufficient confidence.

### 6.1. Build Often Not Required

A full build MAY be skipped for narrow changes such as:

- copy/text;
- CSS;
- spacing;
- visual styling;
- small icon changes;
- isolated layout adjustments;
- narrow frontend presentation changes;
- very local logic changes when the development runtime already compiles them reliably.

Typical flow:

`Change → targeted validation → HMR/reload → test`

### 6.2. Build Recommended or Required

A build SHOULD be considered when changing:

- types/interfaces;
- dependencies;
- package configuration;
- bundler configuration;
- environment handling;
- module/import structure;
- shared libraries;
- API contracts;
- backend compilation;
- database integration;
- routing/bootstrap architecture;
- Electron/Tauri/native layer;
- application startup;
- production runtime behavior;
- code that may fail only during compile/build.

Typical flow:

`Change → validate → build → apply latest runtime → health check`

Do not package merely because a build was run.

---

## 7. Validation Strategy

After coding, run validation proportional to the change.

Possible checks:

- targeted tests;
- unit tests;
- typecheck;
- lint;
- compile;
- build;
- integration tests;
- smoke tests;
- health checks;
- runtime verification.

Principles:

1. Start with the smallest meaningful check.
2. Broaden validation when risk is higher.
3. Run mandatory project checks.
4. Do not run expensive full suites mechanically when targeted checks are sufficient.
5. Do not skip meaningful validation simply to save time.
6. Fix failures caused by the current task.
7. Report unrelated pre-existing failures instead of silently expanding scope.

---

# 8. Development Reload Strategy

Use the **least disruptive mechanism that makes the latest code active and testable**.

Priority:

`HMR / Live Reload → Reload UI → Restart affected service → Restart whole app`

The agent SHOULD choose the lowest level that reliably applies the change.

---

## 8.1. Level 1 — HMR / Live Reload

Prefer HMR or live reload for ordinary frontend changes when supported and reliable.

Typical examples:

- CSS;
- layout;
- text;
- icons;
- frontend components;
- many local frontend logic changes.

Desired behavior:

`Save code → HMR applies change → UI updates automatically`

The user should not need to manually restart the application for ordinary frontend work.

If HMR successfully applies the latest code, do not restart more of the application.

---

## 8.2. Level 2 — Reload UI

Use a clean frontend/UI reload when:

- HMR is unavailable;
- HMR failed;
- HMR preserved stale state;
- initialization behavior needs testing;
- cached frontend state is suspicious;
- a clean page/app-shell load is required.

Possible mechanisms:

- `F5`;
- `Ctrl+R`;
- framework/runtime reload;
- development-only `Reload UI` action;
- browser refresh.

A UI reload SHOULD:

- reload the frontend;
- avoid restarting the backend when unnecessary;
- avoid rebuilding when unnecessary;
- avoid restarting the whole application when unnecessary.

For Tinix desktop applications, a development-only `Reload UI` action MAY be provided when useful.

---

## 8.3. Level 3 — Restart Affected Service

Restart only the service whose runtime must load new code.

Examples:

### Restart backend when changes affect:

- API code;
- server logic;
- backend configuration;
- backend environment variables;
- database integration;
- backend dependency loading;
- shared code consumed by backend runtime;
- server bootstrap;
- backend runtime state.

### Restart frontend dev server when:

- dev-server configuration changed;
- bundler configuration changed;
- environment variables were loaded only at startup;
- HMR cannot apply the change;
- frontend process is stale or unhealthy.

Do not restart backend for a frontend-only change unless the project runtime actually requires it.

---

## 8.4. Level 4 — Restart Whole Application

Restart the full application only when necessary.

Typical triggers:

- desktop main-process changes;
- Electron/Tauri/native changes;
- native modules;
- startup logic;
- launcher changes;
- application bootstrap changes;
- process-level configuration;
- dependency loading that occurs only at startup;
- runtime changes that cannot be applied with frontend/backend reload alone.

Do not use full app restart as the default response to ordinary UI changes.

---

## 8.5. Core Reload Rule

**Do not restart more of the application than necessary.**

The objective is:

- latest code active;
- minimal interruption;
- fast feedback;
- reliable test state.

---

## 9. Manual Refresh Is a Fallback, Not the Primary Workflow

The preferred user experience is automatic update through HMR/live reload.

Manual refresh exists for recovery and clean-state verification.

Recommended order:

1. Automatic HMR/live reload.
2. Manual `Reload UI`, `F5`, or `Ctrl+R`.
3. Restart affected runtime.
4. Restart whole application.

The user SHOULD NOT need to press refresh after every normal frontend edit.

---

## 10. Preserve Test Context When Reasonable

Fast iteration benefits from preserving useful state.

HMR MAY preserve:

- current screen;
- selected item;
- panel state;
- form state;
- navigation context.

However, preserved state can hide initialization problems.

Therefore:

- use HMR for rapid iteration;
- use clean UI reload when validating initialization;
- use full restart when validating startup behavior.

Do not assume HMR proves that a clean application launch works.

---

## 11. Restart Timing

Do not restart after every tiny intermediate edit.

During implementation:

1. make the necessary edits;
2. run relevant validation;
3. apply/restart the affected runtime after the final meaningful change;
4. verify the runtime;
5. leave the app ready for user testing.

An earlier restart is appropriate when needed for debugging.

---

## 12. Process Ownership and Duplicate Instances

Before stopping or restarting a process, identify the correct project-owned process.

Prefer evidence such as:

- project-owned PID file;
- command line;
- working directory;
- executable path;
- process metadata;
- runtime-specific ownership information;
- port plus additional ownership evidence.

A port number alone SHOULD NOT be treated as sufficient evidence when safer ownership information exists.

Do not:

- kill all Node processes;
- kill all Python processes;
- kill unrelated development servers;
- terminate unrelated applications.

Avoid:

- duplicate frontend servers;
- duplicate backend servers;
- duplicate desktop app instances;
- stale processes holding ports;
- zombie processes;
- testing against an old runtime.

---

## 13. Development Scripts

Projects SHOULD provide simple, predictable development entry points when useful.

Use the platform and stack that fit the project.

Examples:

### Windows

- `.bat`
- `.cmd`
- `.ps1`
- `.vbs`

### Cross-platform / runtime-specific

- package-manager scripts;
- shell scripts;
- task runners;
- Makefile targets;
- framework-native dev commands.

Possible entry points:

```text
DEV_START
DEV_STOP
DEV_RESTART
DEV_STATUS
```

or a single idempotent restart command.

Do not create multiple overlapping scripts when the repository already has a reliable workflow.

---

## 14. Restart Script Requirements

If a project provides a restart script, it SHOULD be safe to run repeatedly.

A good restart workflow can:

1. identify project-owned processes;
2. stop only affected processes;
3. wait for shutdown when necessary;
4. release required ports/resources;
5. start required services;
6. avoid duplicate instances;
7. preserve or write useful startup logs;
8. verify startup;
9. return a clear success/failure status.

Prefer **idempotent** scripts.

Running the same restart command twice SHOULD NOT leave duplicate runtimes.

---

## 15. Do Not Replace Existing Workflow Without Need

Before adding a new development script or process manager, inspect whether the repository already has:

- start command;
- dev command;
- restart command;
- build command;
- test command;
- health check;
- process manager;
- PID management;
- launcher;
- framework-native HMR.

If the existing workflow is reliable, extend or repair it rather than creating a parallel system.

Do not break commands the user already relies on without a concrete reason.

---

## 16. Health Check After Restart

A successful start command is not sufficient proof that the application is ready.

After a runtime restart, verify the relevant runtime.

### Frontend

Possible checks:

- dev server is listening;
- expected frontend URL responds;
- app window loads;
- UI bundle loads;
- no fatal startup error;
- latest relevant code is active.

### Backend / Service

Possible checks:

- process is alive;
- expected endpoint responds;
- health endpoint passes;
- relevant API responds;
- no crash loop;
- no fatal startup exception.

Prefer project-defined health endpoints such as:

```text
/health
/api/health
/ready
/status
```

when available.

### Desktop / Native App

Possible checks:

- app process starts;
- application window opens;
- required local services are healthy;
- UI loads;
- startup logs contain no fatal error.

---

## 17. Verify That the Latest Code Is Running

A runtime can be healthy while still running stale code.

When relevant, verify freshness using the strongest available evidence:

- HMR confirmation;
- process start time after the final change;
- build/revision ID;
- version/fingerprint;
- changed behavior;
- runtime log;
- development server rebuild confirmation.

Do not rely only on:

- browser refresh;
- successful build;
- process existence.

The goal is to verify that the user is testing the current implementation.

---

## 18. Startup or Restart Failure

If restart/startup fails:

1. inspect the relevant log or error;
2. determine whether the failure is related to the current task;
3. fix it when within scope;
4. rerun relevant validation;
5. retry the minimum required runtime action;
6. rerun the health check.

Do not report the app as ready to test if required verification failed.

If blocked by an unrelated or unsafe issue, report:

- failed command/action;
- main error;
- affected runtime;
- what still works;
- remaining blocker.

Do not hide a failed restart behind a successful build.

---

## 19. Avoid Unnecessary Desktop Disruption

When developing local applications:

- avoid opening duplicate terminal windows;
- avoid opening duplicate browser tabs;
- avoid spawning new windows every iteration;
- clean up project-owned stale processes;
- prefer background processes when appropriate;
- preserve the user's current testing context when possible.

Development automation SHOULD make testing easier, not create desktop noise.

---

## 20. Long-Running and Data-Sensitive Work

Do not automatically interrupt important running work merely to restart a runtime.

Examples:

- export;
- render;
- batch processing;
- upload;
- migration;
- transcoding;
- long-running user job;
- data mutation.

If restart would interrupt data-sensitive work:

- follow the project-specific policy;
- wait for a safe point when appropriate;
- use a restart mechanism that preserves work if available;
- report the conflict if it cannot be resolved safely.

A project MAY explicitly authorize interruption for specific development-only jobs, but that authorization should be documented in the Project Runtime Profile or another project-specific rule.

---

## 21. Environment and Configuration Changes

Environment/config changes often require stronger runtime actions than ordinary code changes.

When changing:

- `.env`;
- runtime environment variables;
- bundler configuration;
- server configuration;
- native configuration;
- dependency resolution;
- process startup settings;

determine which processes read those values only at startup.

Restart only those affected processes.

Do not assume HMR reloads startup-time configuration.

---

## 22. Development-Only Controls

A project MAY expose development-only controls such as:

```text
Reload UI
Restart Backend
Restart App
Open Logs
Show Runtime Status
```

These controls can improve manual testing.

Rules:

- development controls MUST NOT appear unintentionally in production;
- `Reload UI` SHOULD be the least disruptive option;
- destructive or high-impact developer actions SHOULD be clearly distinguished;
- developer controls SHOULD reuse the project's normal restart mechanisms rather than invent a second runtime path.

---

## 23. Optimize the Development Loop

The goal is fast feedback without sacrificing correctness.

Avoid the default workflow:

`Change → clean everything → full test suite → full build → package → installer → full restart`

for a small UI change.

Prefer:

`Change → targeted validation → HMR/reload → verify`

For larger changes:

`Change → tests/typecheck → build → restart affected runtime → health check`

For startup/native/package changes:

`Change → relevant validation/build → restart full app or package when justified → verify`

---

## 24. Packaging and Release

Packaging is a separate phase from normal development.

Package when:

- explicitly requested;
- validating packaged behavior;
- preparing a release;
- testing installer/distribution;
- changing packaging, updater, signing, native distribution, or deployment behavior.

For release work, additional checks MAY include:

- clean build;
- full test suite;
- package generation;
- installer test;
- upgrade test;
- signing;
- release notes;
- version validation;
- deployment verification.

Do not impose release-level work on ordinary development iterations.

---

## 25. After Each Coding Task

Before reporting a coding task complete, ensure when applicable:

- requested change is implemented;
- relevant validation passed;
- build ran if justified;
- latest frontend code is active;
- affected backend/service was restarted if needed;
- whole app was restarted only if needed;
- duplicate project runtimes are not obviously present;
- relevant health verification passed;
- application is ready for user testing.

The final report SHOULD stay concise.

Include when relevant:

- what changed;
- validation performed;
- whether build ran;
- reload/restart action;
- runtime/health status;
- blocker or known limitation.

---

## 26. Default Decision Matrix

Use this as a default guide.

| Change type | Validate | Build | Runtime action | Package |
|---|---|---|---|---|
| Text / CSS / spacing | Targeted check | Usually no | HMR | No |
| Normal frontend component | Targeted test/typecheck as relevant | Usually no | HMR → Reload UI if needed | No |
| Frontend env/dev-server config | Relevant check | Maybe | Restart frontend dev server | No |
| Backend/API logic | Targeted tests | Maybe | Restart backend/service | No |
| Shared frontend/backend code | Tests/typecheck | Often useful | Restart affected runtimes | No |
| Dependency change | Typecheck/tests | Usually yes | Restart affected runtimes | No |
| Build/bundler config | Relevant checks | Yes | Restart affected runtime | No |
| Desktop main/native process | Relevant tests/build | Usually yes | Restart whole app | No |
| Startup/bootstrap | Relevant tests/build | Usually yes | Restart whole app/service | No |
| Packaging/installer/updater | Release-relevant checks | Yes | As required | Yes |
| Release | Full required checks | Yes | Clean runtime verification | Yes |

This matrix is guidance. Project-specific runtime behavior takes precedence.

---

## 27. Default Development Loop

Use this default loop:

```text
Inspect current runtime
↓
Implement change
↓
Run targeted validation
↓
Run build only when justified
↓
Apply latest code using the least disruptive mechanism
    HMR
    ↓ if insufficient
    Reload UI
    ↓ if insufficient
    Restart affected service
    ↓ if required
    Restart whole app
↓
Verify latest code is active
↓
Health check relevant runtime
↓
Fix introduced runtime/startup issues if necessary
↓
Leave application ready for user testing
```

Packaging is **not part of this default loop**.

---

## 28. Core Principle

**Use the smallest action that reliably makes the latest code testable.**

Prefer:

`HMR → Reload UI → Restart affected service → Restart whole app → Package only when justified`

Do not restart, rebuild, or package more of the project than necessary.

---

## 29. LAN Authoritative Server Workflow (Phase 2)

> **Đã lỗi thời (27/10/2026):** `LalabAutoReport.Server` và `LoadHarness` đã gỡ khỏi solution; các lệnh `dotnet run --project src/LalabAutoReport.Server` bên dưới không còn hợp lệ. Giữ lại như lịch sử. Lệnh xác minh hiện hành: `dotnet build LalabAutoReport.slnx -c Debug` và `dotnet test LalabAutoReport.slnx -c Debug`.

### 29.1 Write Gate Mode (`--lan-writes`)
`LalabAutoReport.Server.exe` controls LAN write availability via the `--lan-writes` command-line switch:
- **`--lan-writes orders` (Default):** Kích hoạt các capability vận hành đơn hàng Phase 2: `orders.setDelivered`, `orders.setPrinted`, `orders.editNote`. Các thao tác tài chính, in ấn và bảo trì giữ trạng thái fail-closed trả về `503 WRITES_DISABLED_UNTIL_PHASE2`.
- **`--lan-writes none` (Instant Rollback):** Ngay lập tức hoàn nguyên toàn bộ API ghi về chế độ Phase 1 chỉ đọc an toàn mà không cần thay đổi lược đồ database. Mọi lệnh ghi đều nhận `503 WRITES_DISABLED_UNTIL_PHASE2`.

### 29.2 Chạy Server trên bản copy DB tạm (Isolated Temp DB)
Để tránh ô nhiễm database môi trường thật hoặc `%ProgramData%`:
```powershell
# 1. Tạo thư mục tạm và copy dev database
$tempDir = Join-Path $env:TEMP ("lalab_srv_dev_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDir -Force
Copy-Item "artifacts/acceptance/profile/lalab_autoreport.db" "$tempDir\lalab_autoreport.db"

# 2. Khởi động Server với cổng loopback riêng biệt
dotnet run --project src/LalabAutoReport.Server -- `
    --data-dir "$tempDir" `
    --port 5050 `
    --health-port 5099 `
    --lan-writes orders
```

### 29.3 Kiểm tra sức khỏe & Độ sẵn sàng (Health & Readiness)
- **Liveness Probe:** `GET http://127.0.0.1:5099/health/live`
  - Trả về `200 OK` ngay khi host ASP.NET Core đang chạy:
    ```json
    { "status": "Live", "buildId": "...", "startedAtUtc": "..." }
    ```
- **Readiness Probe:** `GET http://127.0.0.1:5099/health/ready`
  - Đánh giá động: file lock `server.lock` đã chiếm, migration database đạt phiên bản mới nhất (`schema_migrations_max_version >= 29`), cheap DB query probe thành công, và Kestrel Mobile Server đang hoạt động.
  - Trả về `200 OK` kèm danh sách components khi sẵn sàng phục vụ; trả về `503 Service Unavailable` khi đang khởi động hoặc gặp lỗi cơ sở dữ liệu.

### 29.4 Quy trình kiểm thử khói B3 (B3 Manual/Scripted Smoke Procedure)
Quy trình kiểm tra nhanh tính năng Phase 2 từ dòng lệnh:

1. **Đăng nhập cấp phiên (Login):**
   ```bash
   curl -X POST http://127.0.0.1:5050/api/v1/auth/sessions \
     -H "Content-Type: application/json" \
     -d '{"pin":"888888","clientKind":"desktop"}'
   # Kỳ vọng: 200 OK, trả về token, sessionId, role="Admin"
   ```

2. **Đọc trạng thái vận hành & Revision:**
   ```bash
   curl -X GET http://127.0.0.1:5050/api/v1/orders/1002/operational-state \
     -H "Authorization: Bearer <TOKEN>" \
     -H "X-Lalab-Client-Api: 1.0"
   # Kỳ vọng: 200 OK, trả về object delivered, printed, note với revision hiện tại
   ```

3. **Gửi lệnh cập nhật có số hiệu revision (Commands):**
   ```bash
   # Giao hàng:
   curl -X POST http://127.0.0.1:5050/api/v1/orders/1002/delivered \
     -H "Authorization: Bearer <TOKEN>" -H "X-Lalab-Client-Api: 1.0" -H "Content-Type: application/json" \
     -d '{"operationId":"a1b2c3d4e5f6478980123456789abcde","expectedRevision":0,"delivered":true}'
   # Kỳ vọng: 200 OK, revision tăng lên 1

   # Ghi chú:
   curl -X POST http://127.0.0.1:5050/api/v1/orders/1002/note \
     -H "Authorization: Bearer <TOKEN>" -H "X-Lalab-Client-Api: 1.0" -H "Content-Type: application/json" \
     -d '{"operationId":"b2c3d4e5f6a1478980123456789abcde","expectedRevision":0,"note":"Smoke note test"}'
   # Kỳ vọng: 200 OK, note_revision tăng lên 1
   ```

4. **Kiểm tra tính bất biến / phát lại cùng thao tác (Idempotent Replay):**
   - Gửi lại chính xác lệnh ghi chú ở bước 3 (cùng `operationId`, cùng `expectedRevision`, cùng payload).
   - Kỳ vọng: `200 OK`, trả về kết quả cũ đã commit, không tăng revision lần hai.

5. **Kiểm tra phát hiện xung đột số hiệu (409 STALE_REVISION):**
   - Gửi lệnh cập nhật ghi chú với `expectedRevision: 0` (trong khi revision thực tế trên server hiện tại đã là 1).
   - Kỳ vọng: `409 Conflict`, mã lỗi `STALE_REVISION`, kèm `currentRevision: 1`.

6. **Kiểm tra ranh giới cổng ghi (503 Service Unavailable):**
   ```bash
   curl -X POST http://127.0.0.1:5050/api/bills/1/toggle-payment -d ""
   # Kỳ vọng: 503 Service Unavailable, mã lỗi WRITES_DISABLED_UNTIL_PHASE2
   ```

7. **Thu hồi toàn bộ phiên (Admin Revoke-All):**
   ```bash
   curl -X POST http://127.0.0.1:5050/api/v1/admin/sessions/revoke-all \
     -H "Authorization: Bearer <TOKEN>" -H "Content-Type: application/json" -d '{}'
   # Kỳ vọng: 200 OK. Thao tác tiếp theo bằng token cũ sẽ bị từ chối với 401 Unauthorized.
   ```

