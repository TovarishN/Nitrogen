# Real CSharp5 consumer preflight

This is mechanism evidence, not CSharp5 source admission. CSharp1-4 production grammars and dependency pins are unchanged in the CSNitrogen source checkout.

An isolated CSNitrogen clone at12bab5a78793ffb179b0454f22692df475fb5cfe records the local proof in commit b9350e9. The test assembly substitutes copies of Core, Declarations, Lambdas and AnonymousMethods, retaining the other26 baseline grammar modules. Its AsyncFunctions and AwaitExpressions additions are independently removable. The command uses the candidate NitrogenRoot; no handwritten CSharp parser or source rewriting is involved.

The desired owner assertions first failed5 of18 targeted tests with the old global-await probe. Scoped production grammar/compiler support and scoped entry branches pass41 of41 tests: three InvocationExpression owners and five AwaitExpression owners in ordinary/async methods, mixed files, lambdas and nested anonymous methods; verbatim and escaped await remain invocations; await operands preserve(M()) and source UTF16 ranges; original Parameters/Body field types and direct parents survive independent module omission; adding/removing an async header matches full trees and reuses unaffected nodes.

The local preflight exposed missing named fields in a guarded root ordered choice. Context-using root choices with identical material layouts now retain the common fields in the existing owner frame. Legacy choices and differing layouts retain their prior shapes. Core compiler/generated fixtures separately test this projection, rollback, explicit false scopes, recovery parity and equal-length incremental header edits.

Reproduction command (NitrogenRoot must be a checkout containing this branch):

```sh
dotnet test tests/CSNitrogen.CSharp5.Tests/CSNitrogen.CSharp5.Tests.csproj -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false -p:NitrogenRoot=/path/to/Nitrogen
```

The source corpus, lexical spelling boundaries beyond these witnesses, source-local restrictions, Unicode profile policy and final version admission remain separate CSharp5 work. No compiler-conformance claim follows from these41 checks. The proof is integrated into a dependent CSNitrogen PR after core review.
