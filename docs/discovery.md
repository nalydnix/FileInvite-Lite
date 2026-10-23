# Discovery notes

Template for notes from talking to someone who chases documents for a living — a mortgage broker,
a conveyancer, an accountant, an onboarding or compliance team, a letting agent.

The point is to find out where the real work is, so the prototype is shaped by the job rather than
by what is easy to build. Most of what makes a document-collection tool good or bad is invisible
from the outside: who chases, how often, what they do when a client sends the wrong thing, and
what they are actually afraid of.

**How to use this:** copy the template per conversation, fill it in within a few hours while the
wording is fresh, and keep the person's own phrasing. Quotes are worth more than summaries — a
sentence in their words will settle a design argument that a paraphrase will not.

---

## Before the conversation

- 30–40 minutes.
- Ask about the **last time** they did the thing, not what they "usually" do. Recall beats
  generalisation.
- Ask what happened, not what they want. A feature request is an answer; you want the problem.
- Do not demo. If you show the prototype, do it in the last five minutes, after you have their
  version of the story.
- Record only with permission. No client names, no real document contents in these notes.

---

## Template

```markdown
### Conversation N — YYYY-MM-DD

**Who:** role, organisation type, team size, how long in the job
**Volume:** requests per week, documents per request, typical turnaround
**Tools today:** email / portal / shared drive / a product — which, and what it is called

#### The last request they sent
Walk it start to finish, in their words.
- What were they collecting, and why?
- How did they ask? (template? phone call first?)
- What came back, and how long did it take?
- What went wrong?

#### Chasing
- Who chases, and when do they decide it is time?
- How? (email, phone, text, through someone else)
- How many chases before a typical client responds?
- What do they say in a chase? Does the wording change the third time?
- What makes them give up or escalate?

#### When something is wrong
- How often is a document unreadable, out of date, the wrong one, or incomplete?
- How do they tell the client? What do they actually write?
- Does the client understand what to fix?
- What is the worst version of this they have had?

#### Deadlines
- Where does a due date come from? Is it real or a nudge?
- What happens when it passes?
- Who is chasing *them* about it?

#### Security and trust
- What are they not allowed to do with these documents?
- Has a client ever refused to send something? What did they say?
- Has anything gone to the wrong person?
- Who else needs to see the file afterwards, and for how long is it kept?

#### Their language
Words they use for: the request, the items, the client, the state of things.
(We should use their words in the UI, not ours.)

#### Quotes
> Verbatim. Especially anything surprising, irritated, or resigned.

#### What surprised me
The thing I would not have guessed.

#### What this changes
- Confirms: …
- Contradicts: …
- Open question: …
- Possible change to `docs/spec.md`: …
```

---

## Questions this project needs answered

Specific things the current design assumes. Each one is a guess until someone who does this work
confirms it.

| Assumption in the design | What to ask | Where it would change things |
|---|---|---|
| A checklist of discrete items is how people think about a request | "How do you list what you need? One email, a numbered list, attachments?" | The whole request-builder model |
| One due date for the whole request | "Is everything due at once, or do some things need to come first?" | Per-item due dates |
| Rejecting an item with a comment is enough to get a correct re-upload | "When you send something back, what do you write? Does it work?" | Reject flow, canned reasons, examples |
| Staff review each document individually | "Do you check them one at a time, or all at once at the end?" | Bulk approve, review queue |
| Automatic reminders are wanted | "Would you let a system chase for you? What would you not let it say?" | Reminder cadence, tone, opt-out, staff approval before sending |
| Clients will use a link rather than reply by email | "What happens when a client just replies with an attachment?" | Email ingestion — currently out of scope entirely |
| A magic link with no login is acceptable to them and their clients | "Would you be comfortable sending a link that opens without a password?" | ADR 0004; might need a second factor |
| Completion is a clean moment worth a webhook | "What happens the moment everything is in? Who needs to know?" | Webhook payload, or whether completion is even an event |
| 20 MB and six file types cover the real traffic | "What is the biggest or strangest file a client has sent you?" | Upload limits, HEIC and photos from phones |
| Clients do not need to download what they sent | "Do clients ever ask for a copy back?" | S3.14, which currently forbids it |

---

## Synthesis

After three or more conversations, fill in once:

- **What everyone said:**
- **Where they disagreed, and why:** (often a difference in volume or regulation)
- **What we got wrong in the plan:**
- **What we should build that is not in the spec:**
- **What is in the spec that nobody needs:**
- **Changes made to `docs/spec.md` as a result:**
