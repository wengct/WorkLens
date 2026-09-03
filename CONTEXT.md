# WorkLens

WorkLens collects local work evidence and turns it into reviewable work reports.

## Language

**Prompt template**:
A named, reusable set of AI report-organizing instructions. One active template is the system default, and archived templates remain available only for historical traceability.
_Avoid_: Prompt setting, prompt preset

**Schedule definition**:
The saved rule that determines when one report or backup activity runs and, for reports, which prompt template it uses.
_Avoid_: Cron job, timer

**Schedule execution**:
The durable record of one scheduled or manually triggered run, including its stage outcomes and the prompt snapshot used.
_Avoid_: Background job, run log
