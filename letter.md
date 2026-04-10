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


## Personal workflow with LLMs
Couple of things that I have found tend to work well:
- Spend a bunch of time back and forth with the model just writing out docs and specs. Then make them as consise as reasonable, and read through them again. You will need to restart the session. You need a short concise and exhaustive way to onboard new sessions/models. Many of us have had the experience of having to on-board ourselves in documentation-less codebases. We now have access to world-class documentation writers. No more excuses
- Just start a new session at ~100k. It's not worth it beyond that, regarding the model. Compaction isn't perfect and with a good enough onboarding doc, you dont need it.
- Try to keep one session to one task (and fixing its errors). See above as to why. Essentially the implement -> verify -> repair loop on a per-task basis.
- Always tell models "ask me questions if something is unclear, don't assume". Closest to a silver bullet I've seen so far
- Models usually remember this, but ask for unit tests. Also after each phase make them audit and fix issues. Then start a new session and audit again.


## My Hot takes and ramblings
LLMs are stupid. Like, really stupid. And I'm not saying that just because I use small models. I've used Codex 5.3, GPT-5.4, Sonnet 4.6, Opus 4.6, Kimi K2.5, GLM-5, GLM-5.1, Minimax M2.7 among many small local ones. They all can't code to save their life. At the same time, I am more than convinced that manual code writing is essentially dead.

When you treat an LLM like an intern that has all the knowledge they could possibly need to write perfect code, but none of the wisdom to make good decisions about what, where and how to actually use and implement, those things *shine*. But you need to remember that they are just an auto-complete on steroids, they get really heavily steered from context, sys prompts and user prompts, they should only be told specifically what, where and how in this instance. You need to know that.

"So junior's are screwed". I personally don't quite agree, but the shift is very real in what a junior is and needs to be. Just knowing syntax and how ot center a div, which I admittedly have given up on years ago, isn't enough anymore. You need to learn architecture and algorithms now, as a bare minimum. You need to understand the frontend and the backend, regardless in which part you will work. Good architecture means the LLMs also are more performant and useful in your codebase. Spaghetti means no one know what's happening. We don't have an excuse for spaghetti anymore.

"If you just vibecode, you get rusty and forget things". True. Very true. I also learned an amazing deal. I hate the term but I will use it as it is essentially what I've been doing if you go by the "you don't touch any of the code" definition. While I didn't edit any lines of code in this or most of my projects in the last month, I kept looking at the code that is being written the whole time. Most of it I didn't understand. A good amount I did. I couldn't read rust or go to save my life 2 weeks ago. Now I am starting to identify problematic patterns. I only used tmux with a single window, a couple of tabs inside and a couple of panes in each tab. Now i know about sockets, how good capturing tmux and sending keys is etc. You do gain a lot of "bad habbits" from vibecodding, but if you pay attention, you passively learn stuff you might have not known or forgot about. Not everyone is a terminal wizard, but now you can almost passively become one just from following a model's train of though. That is worth something.

This might sound like I'm contradicting myself, but try to see the neuance. I think modern models are more than capable enough. Period. We really need to focus on making them efficient, locally runnable and provide them with frameworks and tools better suited for them.

An example of why I think modern models are capable enough is something from this post https://sockpuppet.org/blog/2026/03/30/vulnerability-research-is-cooked/, specifically when they mention "Is the Linux KVM hypervisor connected to the hrtimer subsystem, workqueue, or perf_event? The model knows.". I asked this qwen3.5 35B. It knew. I also asked Gemma 4 E2B and it didn't so at least I am still smarter than a 2B model.

Before I continue let me be clear, I don't think current benchmarking can be taken as a fact. I do think it is useful however to get a rough idea on model performance. There are many problems with current benchmarks (not explicitly stating the harness which accoring to terminal-bench's result can swing scores a lot), us not having a deterministic way of evaluating models, etc.

With that in mind, a good example of *why* we should focus on tooling is https://github.com/itigges22/ATLAS , a project that *doubled* the the benchmarked score of a 14B model on LiveCodeBench, reaching frontier level *for that specific benchmark*. That's insane.


## Next projects (probably)
Orchestration. I have been thinkering with the idea for a while now, there is good research on it. I want to make it work well with small models, combining my two previous findings, namely models being good generalists out of the box and really good specialist when they are inside the correct framework for the job.

Local-first phone assistant model. I have a prototype on my github, i like it, it has major problems, needs a full rewrite. Information is moving too fast for a normal person nowadays, our phone's vram stays asleep most of the time, there might be something useful there.


