# Probably the only non-LLM generated file in this repo

Hi. This little project is the culmination of the last month of so of gathering research, prototyping, and testing every strategy i can find or think of regarding LLMs as agents. I will try (and potentially fail) to be brief and concise, firstly focusing on the project and what I think is interesting/important about it, then I'll expand a bit on my xp with llms for agentic work as someone who's been doing stupid project and prototypes since middleschool.

*Note*: It's all my opinions and experiences. I can be wrong. I often am. I might change my mind on half of the things here in a day. This is a snapshot of where I'm at based on what I've seen so far. All projects I mentioned I adore, they are all made by amazing people and I hope they keep working on cool stuff.


## Little Helper
The spec and all that can be read in the README.md, I want to talk about the behind the scenes here.

This project is build on top of research regarding prompting, orchestration tooling and context management I've collected while testing different hypothesies in the last month or so. The research was in part found by LLMs and in part by me, summarized by LLMs.

It is build by (the not-so small) model GLM-5.1 by Z.AI. I have my notes, but a cool and useful model.

You might notice its written in C# and not the usual typescript/python/rust trifecta that seems to have taken hold of all those tools. There is a cool benchmark, namely https://github.com/Tencent-Hunyuan/AutoCodeBenchmark that pinned a bunch of models against eachoter on ~200 similar tasks in a bunch of languages, and C#, somewhat surprisingly for me, was one of the best "LLM writes this correctly" languages. I decided to use that info for a project I don't plan on touching the code for too much.

For the harness, i used hermes for this particual project, like it so far, have my notes on it which I will mention in the other section.


## My Research and take on the current landscape

I like using stuff I have direct access to and "own". It's not a *hard* requirement, but I prefer using local stuff whenever I can. This project's focus is on that, a harness that, according to my research, prototypes and testing, *should* work well with smaller/local models.

The final phase of my research gathering was checking out what people think of the harnesses I've enjoyed using the most in the last couple of weeks, collecting their feedback, trying to pin down local/small model specific feedback, etc. This was done by a locally running Qwen3.5 35B. Surprisingly capable little fella.

I kind of bin current research into two buckets, the "We need to give as much tools to the models" and "The models understand enough, give them bash and watch them do wonders". Personally, in my testing, neither is wrong per-se, but it *really* feels like the truth is in using both. In the next section I have examples of both, asking a "bare" chat model a highly technical question, and a small model with a framework around it doubling (according to a benchmark) it's coding prowess. If only both can utilized..

The harnesses I've used the most are opencode, pi, forgecode and now hermes. Pi is very heavily in the "just give it bash" camp, the other three lean toward the "have a tool/skill for every occasion". From what I gathered, which i kind of expected, smaller models (14B-35B) tend to be more coherent in pi, specially when locally hosted and you can't afford 12k of just sys prompt + tools/skills. By comparison pi is around 2k. Little helper is around 1k but by the time you are reading this, its probably bigger. What I also noticed is that all 4 have tool-calling problems according to the feedback, and not only on the smaller models.


## My Hot takes and ramblings
I think modern models are more than capable enough. Period. We really need to focus on making them efficient, locally runnable and provide them with frameworks and tools better suited for them.

An example of why I think modern models are capable enough is something from this post https://sockpuppet.org/blog/2026/03/30/vulnerability-research-is-cooked/, specifically when they mention "Is the Linux KVM hypervisor connected to the hrtimer subsystem, workqueue, or perf_event? The model knows.". I asked this qwen3.5 35B. It knew. I also asked Gemma 4 E2B and it didn't so at least I am still smarter than a 2B model.

Before I continue let me be clear, I don't think current benchmarking can be taken as a fact. I do think it is useful however to get a rough idea on model performance. There are many problems with current benchmarks (not explicitly stating the harness which accoring to terminal-bench's result can swing scores a lot), us not having a deterministic way of evaluating models, etc.

With that in mind, a good example of *why* we should focus on tooling is https://github.com/itigges22/ATLAS , a project that *doubled* the the benchmarked score of a 14B model on LiveCodeBench, reaching frontier level *for that specific benchmark*. That's insane.


## Next projects (probably)
Orchestration. I have been thinkering with the idea for a while now, there is good research on it. I want to make it work well with small models, combining my two previous findings, namely models being good generalists out of the box and really good specialist when they are inside the correct framework for the job.

Local-first phone assistant model. I have a prototype on my github, i like it, it has major problems, needs a full rewrite. Information is moving too fast for a normal person nowadays, our phone's vram stays asleep most of the time, there might be something useful there.


