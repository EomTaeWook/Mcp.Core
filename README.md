# MCP.Core

MCP 도구 서버의 HTTP 전송·JSON-RPC·컨트롤러 라우팅을 제공하는 .NET 10 클래스 라이브러리다. Dignus 1.4.3의 DI와 Dignus.Actor.Core 2.1.12의 요청 액터를 사용하며 ASP.NET Core·Avalonia·Eidolon App/Core에 의존하지 않는다. 독립 솔루션은 `MCP.Core.slnx`, 프로젝트는 `MCP.Core.csproj`, 기본 네임스페이스는 `MCP.Core`이며 공개 타입은 기능별 하위 네임스페이스에 둔다. 라이선스는 [MIT](LICENSE)다. Dignus.Actor.Core와 전이 의존성 Dignus.Actor.Abstractions도 [MIT](https://github.com/EomTaeWook/Dignus.ActorServer/blob/4ae7647d68a0594e04c9ae37e822be62ef686d05/LICENSE)다.

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
using MCP.Core.Models.Server;
using MCP.Core.Models.Tools;
using MCP.Core.Tools;
using MCP.Core.Transport;
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
using MCP.Core.Attributes;
using MCP.Core.Controllers;
using MCP.Core.Models.Protocol;
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

## 요청별 추가 입력 왕복

같은 도구 핸들러가 공식 [dual-era 예제](https://github.com/modelcontextprotocol/typescript-sdk/tree/main/examples/dual-era)처럼 2025 초기화 방식과 2026 요청별 방식을 지원한다. `2026-07-28`의 `tools/call`은 공식 [Multi Round-Trip Requests](https://modelcontextprotocol.io/specification/2026-07-28/basic/patterns/mrtr)를 사용하며 HTTP POST·JSON 응답으로 처리한다. 이 버전에서는 세션·독립 서버 JSON-RPC 요청을 만들지 않는다. 입력 요청 메서드는 `sampling/createMessage`로 제한하며 Sampling은 이 규격에서 비권장으로 지정됐으나 최소 12개월 유지되는 기능이다.

기존 `Func<JsonObject, CancellationToken, Task<McpToolResult>>` 도구 생성자는 유지한다. 새 생성자는 `Func<JsonObject, McpToolCallContext, CancellationToken, Task<McpToolResult>>`를 받는다. `McpToolCallContext`는 해당 요청의 `IsModern`, `IsInputContinuation`, `SessionId`, `ClientCapabilities`, `InputResponses`, `RequestState`, `RequestId`, `AuthenticationScope`를 제공한다. JSON 속성은 요청에서 복사하며 getter도 사본을 반환하므로 호스트가 수정해 원래 기능 선언을 바꿀 수 없다. `AuthenticationScope`는 서버가 검증한 AccessToken의 SHA-256이며 무토큰 서버는 `anonymous`다. 원본 Bearer 토큰을 전달하지 않으며 클라이언트의 자기 신고 정보는 인증 판단에 쓰지 않는다.

호스트는 추가 입력이 필요할 때 `McpToolResult.InputRequired(inputRequests, requestState)`를 반환한다. 2026 응답은 `resultType`, `inputRequests`, 선택적인 `requestState`를 JSON-RPC 결과 바로 아래에 쓰고 일반 도구 content 안에 감싸지 않는다. 입력 요청은 비어 있지 않은 맵이며 각 항목의 `method` 문자열과 `params` 객체를 확인한다. 메서드가 `sampling/createMessage`가 아니거나 `ClientCapabilities.sampling` 객체가 없으면 프로토콜 오류로 거절한다. 2025에서는 정상 초기화를 마친 검증 세션도 필요하다. 생성 업무와 특징·이미지 처리의 의미는 호스트가 소유한다.

클라이언트는 입력 결과를 모은 뒤 새 JSON-RPC ID로 원래 도구 호출을 재시도한다. `inputResponses`와 `requestState`는 `tools/call.params`에 놓으며 `arguments`에 넣지 않는다. `inputResponses`는 객체, `requestState`는 문자열이어야 하며 2025 요청에는 이 두 필드를 허용하지 않는다. 기존 도구 arguments의 입력 스키마 검증은 유지한다. 보낼 결과를 처리할 능력이 없는 클라이언트에 기능을 추정해서 요청하지 않는다.

2025에서는 `initialize.capabilities.sampling`을 선언한 클라이언트에만 암호학적 난수 `MCP-Session-Id`를 반환한다. 이후 요청은 같은 인증과 세션 헤더를 사용하며 `notifications/initialized` 완료 뒤 기능을 사용할 수 있다. capabilities는 초기화 시 사본을 고정하며 세션 수는 `MaximumConcurrentRequests`로 제한한다. 세션은 생성 시점부터 30분간 유효하고 서버 정지 시 해제한다. 만료·없는 세션은 404이므로 새로 초기화해야 한다. Sampling을 선언하지 않은 기존 2025 호출은 세션 없는 일반 도구 호출을 유지한다.

공식 [legacy shim](https://github.com/modelcontextprotocol/typescript-sdk/blob/main/docs/migration/support-2026-07-28.md#legacy-shim-for-input_required)과 [Streamable HTTP](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)의 방식으로 2025 `input_required`를 원래 POST의 SSE `sampling/createMessage` 요청으로 처리한다. 클라이언트는 같은 세션·인증의 별도 POST로 JSON-RPC 응답을 보내며 서버는 202로 접수한다. 서버 RPC ID·세션이 맞는 대기 응답만 한 번 받는다. 잘못된 소속·중복·완료·취소·만료 응답은 거절한다. 각 라운드는 bare result 객체만 `InputResponses`에 넣고 이전 라운드 응답은 대체한다. `requestState`는 바이트에 해당하는 문자열 값을 그대로 전달하고 원 HTTP RPC ID를 유지하며, 라이브러리만 `IsInputContinuation=true`로 재진입한다. 외부 2025 요청의 MRTR 필드는 계속 거절한다. 최대 8라운드이며 클라이언트 오류·거절은 도구 오류 결과로 끝낸다. 마지막 원래 도구 응답을 같은 SSE에 보낸 뒤 닫는다. GET SSE·재연결·이벤트 재생·WebSocket은 제공하지 않는다.

라이브러리는 `requestState`의 의미를 해석하거나 영구 저장하지 않는다. 인증 바인딩·권한·자원 접근·업무 결정을 담은 상태는 호스트가 무결성을 보호하고 만료·원래 요청과의 일치를 확인해야 한다. 원본 요청 ID와 인자, 이미지 내용, 사용자·인증 범위·프로토콜 시대·SessionId의 검증 및 결과 승인·거절은 호스트 책임이다. 2026 외부 재시도는 새 RPC ID를 사용하며 2025 내부 재진입은 신뢰 가능한 `IsInputContinuation`과 원 RPC ID를 확인한다. 별도 업무 큐·영구 재개 저장소나 단일 실행 보장은 제공하지 않는다. 실제 클라이언트 왕복·빌드·테스트는 이번 소스 변경에서 실행하지 않았다.

## 전송과 입력 범위

- Streamable HTTP의 JSON 응답과 2025 요청에 연결된 sampling POST SSE를 제공한다. stdio·구 HTTP+SSE·독립 GET SSE 전송은 제공하지 않는다.
- `2026-07-28`: `server/discover`, `ping`, `tools/list`, `tools/call`과 버전·기능 메타데이터 및 HTTP 헤더 검증을 제공한다.
- `2025-11-25`, `2025-06-18`, `2025-03-26`: `initialize`와 `notifications/initialized`, 선언된 sampling의 POST SSE 왕복을 지원한다. GET·DELETE는 405, 알림·수락한 클라이언트 응답은 202로 응답한다.
- 도구 목록은 이름순으로 전체 반환한다. 목록 커서·변경 알림·resources·prompts·elicitation·tasks는 지원한다고 광고하지 않는다. `sampling`은 서버 기능으로 광고하지 않으며 2026 요청별 기능 또는 2025 초기화 기능 선언을 확인한 경우에만 요청한다.
- 정상 결과는 텍스트와 선택적 `structuredContent`다. `new McpToolResult(metadata, pngBytes)`는 같은 메타데이터와 함께 표준 PNG 이미지 content를 반환한다. 현재 프로토콜의 정상·도구 오류 결과에는 `resultType: "complete"`를, MRTR에는 `resultType: "input_required"`를 사용하며 2025 응답 모양은 유지한다. 리소스 링크 전송은 제공하지 않는다.

입력 스키마는 JSON Schema 2020-12의 제한된 부분집합이다. 단일 `type`, `properties`, `required`, Boolean `additionalProperties`, `items`, `enum`, 숫자·문자열·배열 길이 제한을 사용한다. `title`, `description`, `default`, `examples`는 메타데이터다. `$ref`, 조합 스키마·정규식 등 미지원 키워드는 등록 시 거절한다. 업무 검증은 핸들러가 소유한다.

루프백 주소만 바인딩하고 Host·Origin을 검증한다. 선택적 `AccessToken`은 Bearer 헤더로 검증하고 저장하지 않는다. 기본 제한은 요청 크기 1 MiB, 요청당 2분이다. `MaximumConcurrentRequests`는 접수 중인 HTTP 요청 한도와 요청 액터 수를 함께 정한다. 기본값은 `Math.Min(Environment.ProcessorCount, 128)`이며 호스트는 1~128 사이에서 직접 지정할 수 있다. Windows URL 예약 권한이나 시스템 설정은 자동 변경하지 않는다.

## 요청 액터

HTTP 수신·입력 검증 뒤 일반 요청을 액터에 순환 배정한다. 각 액터는 Dignus의 메일박스에서 하나씩 처리하며 비동기 디스패치가 끝날 때까지 다음 요청을 실행하지 않는다. 컨트롤러는 실행 시점에 기존 Dignus DI에서 `Transient`로 생성한다. 디스패치 결과·예외·취소는 기존 요청 실행 객체의 완료 신호로 HTTP 응답 경로에 전달하며 특정 요청의 실패가 액터를 종료하지 않는다.

액터 수와 같은 수의 디스패처 스레드를 서버 시작 시 생성한다. `MaximumConcurrentRequests = 10`이면 액터·디스패처가 각각 10개다. 호스트 도구의 공유 서비스가 동시에 호출될 수 있으므로 서비스 자체의 동시성 계약은 호스트가 소유한다. 호스트의 생성·학습 등 업무 큐를 병렬 실행하거나 대체하지 않는다.

대기 중인 일반 요청은 기존 요청 슬롯을 점유하며 슬롯이 모두 사용 중이면 `429`와 `Retry-After`를 반환한다. 본문 읽기는 별도의 `MaximumConcurrentRequests` 크기 전송 슬롯으로 제한하며 파싱 뒤 일반 요청 슬롯으로 이동한다. Sampling 응답 POST와 알림은 전송 슬롯만 사용해 일반 요청 슬롯·액터를 우회하므로 모든 일반 요청이 입력을 기다려도 응답·취소를 처리할 수 있다. 전송 슬롯이 사용 중이어도 429를 반환한다. Sampling 대기 중에는 원래 요청 슬롯은 유지하지만 액터는 비워 두며 응답 뒤 도구 재진입을 다시 배정한다. 요청 제한 시간에는 입력 읽기·액터 대기·모델 왕복·실행이 모두 포함된다. 액터가 요청을 받지 못한 경우에도 완료 신호에 오류를 전달한다.

## 수명과 취소

시작·정지·해제는 호스트가 순서대로 호출한다. 시작·종료를 직렬화하는 별도 세마포어는 두지 않으며 중복 시작의 바인딩 오류는 `HttpListener` 예외로 전달한다. 동시 HTTP 요청 수 제한은 기존 요청 슬롯으로 처리한다. 정지 후 다시 시작할 수 있고 해제 후에는 시작할 수 없다.

정지는 HTTP 수락과 대기·진행 중 요청에 취소를 전달하고 실제 HTTP 처리 종료를 기다린 뒤 액터·디스패처를 해제한다. 재시작하면 새 액터 시스템을 만든다. 핸들러는 자신의 하위 작업 종료를 기다려야 하며 서버가 업무 프로세스를 강제 종료하지 않는다. 연결 종료로 이미 접수한 업무를 취소하지 않고 호스트 업무 큐의 수명은 HTTP 요청과 구분한다.

2025 계열과 `2026-07-28` 경로의 `notifications/cancelled`는 같은 `requestId`의 진행 중 HTTP 호출을 취소하며 2025 세션 호출은 세션도 일치해야 한다. Sampling 대기도 원래 요청 취소·제한 시간·세션 만료·서버 종료로 해제하고 대기 RPC 항목을 제거한다. 조회·실행 준비·내보내기 등 모든 도구 호출은 같은 요청별 토큰을 사용한다. `initialize`는 취소 대상으로 등록하지 않는다. 완료 호출과 호스트 큐에 접수한 업무는 취소하지 않는다. 세션 없는 동시 클라이언트는 진행 중 요청 ID가 겹치지 않게 사용한다. `StateChanged`는 호출 스레드에서 발생하므로 UI는 자체 스레드로 전달한다. 오류 콜백에는 원본 예외를 전달하고 클라이언트에는 내부 스택을 노출하지 않는다.
