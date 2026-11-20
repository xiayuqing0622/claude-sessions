// Agent list rows: each subagent's row shows the model it actually runs on, and each
// subagent's context size is left where `cs ctx wait` reads it.
//
// The statusline always follows the lead session, so the model a subagent runs on (fixed at
// spawn, and different from the lead's under CLAUDE_CODE_SUBAGENT_MODEL, say) shows only here.
import type { AgentInfo, Register } from 'claude-code'

const ESC = '\u001b['
const paint = (code: string, text: string) => `${ESC}${code}m${text}${ESC}0m`

// claude-haiku-4-5-20251001 -> haiku-4-5 ; glm-5.3[1m] -> glm-5.3
const shortModel = (model: string) =>
  model.replace(/^claude-/, '').replace(/-\d{8}$/, '').replace(/\[1m\]$/, '')

// Anything that is not an Anthropic family (GLM, Kimi, a gateway model) is yellow, so a
// subagent that ended up on an unexpected model stands out.
const modelColor = (model: string) =>
  /opus/.test(model) ? '35'
  : /sonnet/.test(model) ? '36'
  : /haiku/.test(model) ? '32'
  : /fable/.test(model) ? '34'
  : '33'

const formatTokens = (n: number) => (n >= 1000 ? `${Math.floor(n / 1000)}k` : String(n))

// Ids become path components, so only plain names pass.
const SAFE_ID = /^[A-Za-z0-9_][A-Za-z0-9._-]*$/

const TRANSCRIPT_WINDOW_BYTES = 1024 * 1024

// Providers behind an Anthropic-compatible endpoint (GLM, for one) send zeros in the stream's
// first usage event and the real counts only in the last, so the engine's `tokenCount` of
// their subagents stays near zero. The subagent's transcript records the final usage of every
// response: the context size is the newest non-zero input + cache read + cache creation.
// Returns undefined when the transcript is missing or its last window holds no such record.
const readTranscriptContext = async (
  $: any,
  transcriptPath: string,
  id: string,
): Promise<number | undefined> => {
  const file = `${transcriptPath.replace(/\.jsonl$/, '')}/subagents/agent-${id}.jsonl`
  try {
    const { size } = await $.fs.stat(file)
    const offset = Math.max(0, size - TRANSCRIPT_WINDOW_BYTES)
    const text: string = await $.fs.read(file, { offset, length: size - offset })
    let found: number | undefined
    // The first line of the window may be cut in half; it fails to parse and is skipped.
    for (const line of text.split('\n')) {
      let record: any
      try {
        record = JSON.parse(line)
      } catch {
        continue
      }
      const usage = record?.type === 'assistant' ? record.message?.usage : undefined
      if (!usage || typeof usage !== 'object') continue
      const n =
        (usage.input_tokens ?? 0) +
        (usage.cache_read_input_tokens ?? 0) +
        (usage.cache_creation_input_tokens ?? 0)
      if (n > 0) found = n
    }
    return found
  } catch {
    return undefined
  }
}

// One small file per subagent under cs-ctx/<session id>/, rewritten on every call so its
// modification time says how fresh the numbers are. The file is written under a temporary
// name and renamed, so a reader never sees half a file. The statusline owns cleanup of old
// session directories.
const recordContext = async ($: any, agent: AgentInfo, tokenCount: number) => {
  if (!SAFE_ID.test(agent.id) || !agent.contextWindowSize || agent.contextWindowSize <= 0) return
  const sessionId: string = await $.session.id()
  if (!SAFE_ID.test(sessionId)) return
  const root =
    (await $.env.get('CLAUDE_CONFIG_DIR')) ?? `${await $.env.get('HOME')}/.claude`
  const dir = `${root}/cs-ctx/${sessionId}`
  const doc = {
    name: agent.name ?? agent.description,
    status: agent.status,
    tokenCount,
    contextWindowSize: agent.contextWindowSize,
  }
  const tmp = `${dir}/.${agent.id}.json.tmp`
  await $.fs.write(tmp, `${JSON.stringify(doc)}\n`)
  await $.process.run(['mv', '-f', tmp, `${dir}/${agent.id}.json`])
}

const renderBody = (agent: AgentInfo, tokenCount: number, columns: number) => {
  const name = agent.name ?? agent.description ?? agent.id
  const model = agent.model ? shortModel(agent.model) : ''
  const effort = agent.effort === undefined ? '' : String(agent.effort)
  const percent =
    agent.contextWindowSize && agent.contextWindowSize > 0
      ? ` (${Math.floor((tokenCount * 100) / agent.contextWindowSize)}%)`
      : ''
  const plain =
    `${name} · ${model || '…'}${effort ? ` · ${effort}` : ''} · ${formatTokens(tokenCount)}${percent}`
  // The engine indents the row itself; slack keeps the description from wrapping it.
  const room = columns - plain.length - 8
  // The description is the row's title when there is no name; print it only once.
  const description = (agent.description ?? '').replace(/[\r\n]+/g, ' ')
  const tail =
    description !== '' && description !== name && room > 8
      ? ` · ${description.length > room ? `${description.slice(0, room - 1)}…` : description}`
      : ''
  return (
    paint('1', name) +
    paint('2', ' · ') +
    (model ? paint(modelColor(model), model) : paint('2', '…')) +
    (effort ? paint('2', ` · ${effort}`) : '') +
    paint('2', ` · ${formatTokens(tokenCount)}${percent}`) +
    paint('2', tail)
  )
}

export const register: Register = on => {
  on('ui.render', { component: 'AgentRow' }, async ($, e, next) => {
    const agent = e.props
    let tokenCount = agent.tokenCount
    if (agent.model && !agent.model.startsWith('claude-')) {
      const { transcriptPath } = await $.session.info()
      const fromTranscript = transcriptPath
        ? await readTranscriptContext($, transcriptPath, agent.id)
        : undefined
      if (fromTranscript !== undefined) tokenCount = fromTranscript
    }
    // The state file is a side product: a failure must never cost the row its drawing.
    await recordContext($, agent, tokenCount).catch(() => undefined)
    return next({
      ...e,
      props: { ...e.props, body: renderBody(agent, tokenCount, e.props.columns ?? 80) },
    })
  })
}
