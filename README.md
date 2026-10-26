# MCP.Core

MCP 도구 서버의 HTTP 전송·JSON-RPC·컨트롤러 라우팅을 제공하는 .NET 10 클래스 라이브러리다. Dignus 1.4.3의 DI를 사용하며 ASP.NET Core·Avalonia·Eidolon App/Core에 의존하지 않는다. 독립 솔루션은 `MCP.Core.slnx`, 프로젝트는 `MCP.Core.csproj`, 네임스페이스는 `MCP.Core`다. 라이선스는 [MIT](LICENSE)다.

## 연결

소비 저장소에서 서브모듈로 연결한다.

```powershell
git submodule add --branch main https://github.com/EomTaeWook/Mcp.Core.git Mcp.Core
git submodule update --init --recursive
```

호스트 프로젝트 폴더와 서브모듈이 나란히 있으면 다음과 같이 참조한다. 대상 프레임워크·nullable·암시적 using·경고·결정적 빌드 설정은 이 프로젝트가 직접 소유한다.

```xml
<ItemGroup>
  <ProjectReference Include="../Mcp.Core/MCP.Core.csproj" />
</ItemGroup>
```

독립 솔루션의 빌드 명령은 `dotnet build MCP.Core.slnx`다. 이번 분리 작업에서는 빌드·테스트·실행을 수행하지 않았다.

## 도구 등록

도구 이름·설명·입력 스키마와 핸들러는 호스트가 제공한다. UI·로그·업무 큐·저장소와 생성·학습·DB 기능은 호스트의 책임이다.

```csharp
using MCP.Core;
using System.Text.Json.Nodes;

McpTool tool = new McpTool(new McpToolDefinition("get_status", "Read application status.")
{
    ReadOnly = true,
    Idempotent = true,
    OpenWorld = false
}, (arguments, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult(new McpToolResult(new JsonObject { ["status"] = "ready" }));
});

await using McpHttpServer server = new McpHttpServer(new McpServerOptions
{
    Endpoint = new Uri("http://127.0.0.1:8190/mcp"),
    Name = "Example",
    Version = "1.0.0"
}, new[] { tool });

await server.StartAsync();
// 호스트 작업을 실행한 뒤 await server.StopAsync() 또는 DisposeAsync()로 종료한다.
```

서버 이름은 필수 입력이며 기본값은 비어 있다. 서버 옵션과 도구 정의·스키마는 서버 생성 시 복사하고 실행 중에는 고정한다. 서버 수명과 오류 콜백은 호스트가 소유한다. UI 접근은 해당 UI 스레드로 전달한다.

## 컨트롤러

컨트롤러는 `McpControllerBase`를 상속하고 `[Controller("메서드 이름")]`으로 JSON-RPC 메서드에 연결한다. `MethodName` 상수나 수동 타입 목록은 사용하지 않는다. 기본 컨트롤러는 라이브러리 어셈블리에서 찾고 `controllerAssembly`에 전달한 호스트 어셈블리에서도 추가 컨트롤러를 찾는다. 이름은 대소문자를 구분하며 중복 등록은 서버 생성 시 거절한다.

```csharp
using MCP.Core;
using System.Text.Json.Nodes;

[Controller("example/status")]
public class StatusController : McpControllerBase
{
    private readonly TimeProvider _timeProvider;

    public StatusController(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public override Task<JsonObject> ExecuteAsync(McpRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new JsonObject { ["checkedAtUtc"] = _timeProvider.GetUtcNow().ToString("O") });
    }
}
```

호스트 의존성은 `configureServices`에서 기존 Dignus 컨테이너에 등록한다.

```csharp
await using McpHttpServer server = new McpHttpServer(new McpServerOptions
{
    Endpoint = new Uri("http://127.0.0.1:8190/mcp"),
    Name = "Example",
    Version = "1.0.0"
}, Array.Empty<McpTool>(), controllerAssembly: typeof(StatusController).Assembly, configureServices: services => services.RegisterType<TimeProvider>(TimeProvider.System));
```

모든 컨트롤러 타입은 호스트 의존성 등록 뒤 `LifeScope.Transient`로 등록한다. 서버 구성 시에는 특성·타입만 읽고 인스턴스를 만들지 않는다. 요청·알림마다 DI에서 해당 타입을 resolve해 새 인스턴스를 실행한다. 기본 컨트롤러는 `InitializeController`, `PingController`, `ServerDiscoverController`, `ToolsListController`, `ToolsCallController`, `NotificationsCancelledController`다. 도구 업무는 `tools/call → ToolsCallController → 호스트 핸들러`로 전달한다.

## 전송과 입력 범위

- Streamable HTTP의 JSON 응답 경로를 제공한다. SSE 스트림·stdio·구 HTTP+SSE 전송은 제공하지 않는다.
- `2026-07-28`: `server/discover`, `ping`, `tools/list`, `tools/call`과 버전·기능 메타데이터 및 HTTP 헤더 검증을 제공한다.
- `2025-11-25`, `2025-06-18`, `2025-03-26`: 세션 ID 없는 HTTP 초기화 호환 경로다. `initialize`와 `notifications/initialized`를 받으며 GET·DELETE는 405, 알림은 202로 응답한다.
- 도구 목록은 이름순으로 전체 반환한다. 목록 커서·변경 알림·resources·prompts·sampling·elicitation·tasks는 지원한다고 광고하지 않는다.
- 결과는 텍스트와 선택적 `structuredContent`다. 이미지 바이트·리소스 링크 전송은 현재 범위가 아니다.

입력 스키마는 JSON Schema 2020-12의 제한된 부분집합이다. 단일 `type`, `properties`, `required`, Boolean `additionalProperties`, `items`, `enum`, 숫자·문자열·배열 길이 제한을 사용한다. `title`, `description`, `default`, `examples`는 메타데이터다. `$ref`, 조합 스키마·정규식 등 미지원 키워드는 등록 시 거절한다. 업무 검증은 핸들러가 소유한다.

루프백 주소만 바인딩하고 Host·Origin을 검증한다. 선택적 `AccessToken`은 Bearer 헤더로 검증하고 저장하지 않는다. 기본 제한은 요청 크기 1 MiB, 동시 HTTP 요청 8건, 요청당 2분이다. Windows URL 예약 권한이나 시스템 설정은 자동 변경하지 않는다.

## 수명과 취소

시작·정지·해제는 호스트가 순서대로 호출한다. 시작·종료를 직렬화하는 별도 세마포어는 두지 않으며 중복 시작의 바인딩 오류는 `HttpListener` 예외로 전달한다. 동시 HTTP 요청 수 제한은 기존 요청 슬롯으로 처리한다. 정지 후 다시 시작할 수 있고 해제 후에는 시작할 수 없다.

정지는 HTTP 수락과 진행 중 요청에 취소를 전달하고 실제 처리 종료를 기다린다. 핸들러는 자신의 하위 작업 종료를 기다려야 하며 서버가 업무 프로세스를 강제 종료하지 않는다. 연결 종료로 이미 접수한 업무를 취소하지 않고 호스트 업무 큐의 수명은 HTTP 요청과 구분한다.

2025 계열의 `notifications/cancelled`는 같은 `requestId`의 진행 중 HTTP 호출을 취소한다. 완료 호출과 호스트 큐에 접수한 업무는 취소하지 않는다. 동시 클라이언트는 진행 중 요청 ID가 겹치지 않게 사용한다. `StateChanged`는 호출 스레드에서 발생하므로 UI는 자체 스레드로 전달한다. 오류 콜백에는 원본 예외를 전달하고 클라이언트에는 내부 스택을 노출하지 않는다.
