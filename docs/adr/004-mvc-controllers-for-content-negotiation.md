# ADR-004: MVC controllers + XML formatters for content negotiation

**Status:** Accepted
**Date:** 2026-10-18
**Deciders:** Project owner

## Context
"All endpoints must support both JSON and XML responses based on request headers." This includes error responses. Errors can come from MVC (validation, 404 from actions) or from middleware outside MVC (authentication challenges, unmatched routes, unhandled exceptions).

## Decision
- Use **`[ApiController]` controllers** with `AddXmlDataContractSerializerFormatters()`, `RespectBrowserAcceptHeader = true` and `ReturnHttpNotAcceptable = true` (406 for unsupported `Accept`).
- DTOs in `MarketData.Contracts` carry `[DataContract]`/`[DataMember(Name=…)]` so XML element names are stable and match the JSON camelCase.
- For errors produced outside MVC, register a custom **`XmlProblemDetailsWriter : IProblemDetailsWriter`** ahead of the default JSON writer. It writes when `Accept` prefers `application/xml` (fixes review R5).
- Admin auth is an **API-key `AuthenticationHandler`** (`X-Api-Key`) with `[Authorize(Policy = "Admin")]` on `SymbolsController`. It replaces the "endpoint filter" from the first draft (review R6). Its 401 goes through the ProblemDetails writers above, so it is negotiated too.

## Options Considered

### Option A: Minimal APIs
| Dimension | Assessment |
|---|---|
| Complexity | Low for JSON; High for XML |
| Cost | — |
| Scalability | Equal |
| Team familiarity | High |

**Pros:** Less ceremony; slightly faster.
**Cons:** No output formatters. `Results.Ok(x)` is JSON-only, so XML would need a hand-written `IResult` plus negotiation logic for every endpoint and error.

### Option B: Controllers + built-in XML formatters ✅
| Dimension | Assessment |
|---|---|
| Complexity | Low |
| Cost | — |
| Scalability | Equal |
| Team familiarity | High |

**Pros:** Negotiation, 406 and ProblemDetails-in-XML (through MVC's wrapper providers) are built in.
**Cons:** Some ceremony. Middleware-generated errors still need the custom writer.

### Option C: Minimal APIs + custom negotiating `IResult`
**Pros:** Keeps Minimal APIs.
**Cons:** Re-implements what MVC already provides, with more code and more to test. Not justified.

## Trade-off Analysis
The requirement is about the HTTP contract, not the programming model. B satisfies it with framework features, leaving one small gap (non-MVC errors) that is closed with ~40 lines. A and C would mean owning content negotiation ourselves.

`DataContractSerializer` was chosen over `XmlSerializer` because it handles get-only collections and `DateTimeOffset` predictably. The cost is having to mark DTO members explicitly.

## Consequences
- **Easier:** consistent JSON/XML everywhere, including errors. The console client can switch format with one `Accept` header.
- **Harder:** DTOs need serializer attributes. Nullable members must be emitted as omitted elements (`EmitDefaultValue = false`) so the single-value and series SMA shapes stay clean in XML.
- **Revisit:** if more media types are needed (CSV for exports), add an output formatter. The controllers don't change.

## Action Items
1. [ ] MVC setup in `Program.cs` as above + `AddProblemDetails()`.
2. [ ] `XmlProblemDetailsWriter` + registration order test.
3. [ ] `ApiKeyAuthenticationHandler` + `Admin` policy. Key from `Admin:ApiKey`.
4. [ ] Integration tests for each endpoint: `Accept: application/json`, `application/xml`, `text/csv` (406). Errors 400/401/404 also checked in XML.
