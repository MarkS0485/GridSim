# Why .NET

**Why GridSim is written in C# / .NET 10 - and why that is not the compromise you think it is.**

This document exists because I am tired of having the same conversation. Someone sees a physics-first grid solver - sparse LU, Newton-Raphson, a WLS state estimator, a CPU/GPU compute seam - and their first instinct is *"why isn't this in C++? Or Rust? Or at least Python with the hot loops in native?"* The assumption underneath the question is that a managed runtime is a tax you pay for convenience, and that serious numerical work pays it grudgingly or not at all.

That assumption is wrong, and it is wrong in a specific, defensible way. This is the long-form answer so I never have to give the short one again.

---

## The short answer

Performance is **relative**. It is not a scalar you rank languages by; it is a question - *fast enough for what?* GridSim's answer is unambiguous: it is **more than performant for what this work needs**, with room to spare.

A full power-flow solve on the real GB network lands **sub-second**. That is not "acceptable" - it is **beyond what any deployed grid state estimator does**. Control-room SE runs on a settlement cadence measured in *minutes*; GridSim resolves a state faster than the grid publishes the data to resolve it against. When your engine is already quicker than the operational reality it models, "but a hand-tuned C++ port might shave 200ms" is not an argument. It is a rounding error against a clock that does not care.

So the interesting question was never *how fast can this go*. It was *what runtime lets me build a large, correct, maintainable evidence engine and keep it correct for years*. On that question, .NET wins outright.

---

## Garbage collection is my friend, not my enemy

This is where the received wisdom is most confidently wrong, so let me be blunt: **the GC is an asset here, not a liability.**

The objection everyone reaches for is latency - the dreaded stop-the-world pause. That objection is a decade out of date and it does not apply to this workload anyway:

- **This is throughput compute, not a 60Hz control loop.** GridSim reconstructs historical grid state offline. There is no hard-real-time deadline a collection could blow. The one hard invariant in this codebase is the [169-hour no-forward-inference horizon](UnderstandingRestrictions.md), and that is a *timestamp guard*, not a latency budget. Nobody is dying if a gen-2 collection runs mid-solve.
- **The hot paths don't allocate.** The sparse core is written the way numerical code should be written on .NET: dense flat arrays, `Span<T>` over slices, workspaces allocated *once per factorization* and reused, `Array.Sort(keys, idx, ...)` instead of a comparer delegate + closure per row per iteration. The Gilbert-Peierls LU allocates its `x`, `xi`, `pstack`, `marked` buffers once and grows L/U by doubling. Look in [PerformanceOpts.md](PerformanceOpts.md) - the pattern throughout is *don't allocate in the loop*. The GC's job in a well-written .NET solver is to manage the stuff around the maths - the model graph, the ingest, the results - not the inner kernel.
- **Where I do allocate, I want it managed.** Assembling a 3,539-bus network model, wiring provenance onto every figure, marshalling settlement metering, holding a case library - this is exactly the sprawling object-graph work that a generational GC handles better than I would by hand. I have written the manual-memory version of this in another life. My hand-rolled arena is not going to beat a runtime that has had thousands of engineer-years poured into its collector. It is going to beat *me*, and introduce a use-after-free while it does it.

The GC lets me spend my correctness budget on the physics - machine-epsilon agreement with pandapower, the chi-square consistency gate, the no-forward-inference invariant - instead of on lifetimes. That is not a convenience. On a project whose entire value is *being provably correct*, it is the point.

---

## The frameworks are beyond proven

Here is the fact that the .NET sceptics never sit with:

> **ASP.NET and .NET Core underpin almost every major intranet application in existence.**

Not toys. Not side projects. The line-of-business systems that run banks, insurers, logistics, healthcare back-offices, government departments - the software that has to *not fall over* while carrying an organisation - an enormous share of it is .NET, and has been for twenty years. This is one of the most exercised, most hardened, most boringly reliable runtimes ever shipped. The BCL, the JIT, the GC, the async machinery, the numerics - all of it has been beaten on at industrial scale for two decades and it *holds*.

When I pick .NET 10 for GridSim I am not making a bet. I am standing on a foundation that has already survived everything anyone could throw at it. For an evidence engine - a tool whose credibility depends on nothing quietly going wrong underneath the maths - "beyond proven" is worth more than any benchmark micro-win. I would rather build on the boring thing that a million intranets already trust than on the exciting thing that might.

And the modern runtime is genuinely excellent for this: `double` end-to-end with IEEE-754 you can reason about, first-class `Span<T>`/`Memory<T>` for zero-copy numerics, a real generic maths story, and a clean abstraction seam that lets the **same solver interface** dispatch to CPU or GPU (the AMD-first FP64 path) without the calling code knowing or caring. That last one is not a language people associate with GPU compute - and yet the seam is clean precisely *because* the runtime is good.

---

## The only people who don't like .NET never wrote it

I mean this plainly. In my experience the disdain for .NET is almost entirely from people who have **never actually written it** - who formed an opinion around 2008, filed it under "enterprise Java's Windows cousin," and never reopened the case. They are arguing with a runtime that no longer exists.

They miss the thing that matters: .NET is *productive without being sloppy*. It has the type system and the tooling to keep a large codebase honest, and the ergonomics to let one person move fast across a solver, an estimator, a data pipeline, a CLI, and a Unity front-end without drowning. The people who actually ship in it know this. The people who sneer at it are, almost without exception, describing a language they have not touched.

That is not tribalism. It is an observation about where the criticism comes from. Try building something real in modern .NET and the objection tends to evaporate.

---

## The personal answer

I learned C# in **.NET 1**, and I never looked back. Twenty years of writing it has made it the language I *think* in - the one where the distance between "I understand the problem" and "the code expresses it" is shortest for me.

I will be honest about the flip side, because it is the honest thing to say: I write so much C# now that I can no longer write C or C++ without making a mess. That is not a boast and it is not really a limitation either - it is just true, and it is *relevant to the decision*. The best language for a project is very often the one its author is fluent enough in to keep correct under pressure, at scale, for years. For GridSim that is C#, decisively. Handing this codebase to a language I would fumble, in pursuit of a performance margin the workload does not need, would trade the thing that matters (correctness I can *sustain*) for a thing that does not (headroom I already have).

---

## So: why .NET?

Because performance is relative and this is already faster than the grid it models. Because the garbage collector carries the memory-safety load so my correctness budget goes to the physics. Because the frameworks under it are the most proven in the industry and this is a tool that lives or dies on nothing going quietly wrong. Because the objection to .NET is almost always secondhand. And because it is the language I am fluent enough in to keep a hard, correct thing correct for the long haul.

That is not settling. That is choosing the right tool and knowing why.

> Fast enough is a solved problem. Correct, maintainable, and provable for years is the hard one - and that is the one .NET wins.
