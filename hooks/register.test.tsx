import { expect, test } from 'claude-code/testing'

const SGR = /\u001b\[[0-9;]*m/g

// Nothing sits beneath the plugin: this hook stands for the engine and draws the body the
// plugin set as JSON text, because a Text child may not hold a raw escape sequence.
const mountRow = async (
  $: any,
  on: any,
  props: Record<string, unknown>,
  engine: { transcriptPath?: string; written?: string[]; transcript?: string } = {},
) => {
  on('ui.render', ($: any, e: any) => {
    const { Text } = $.ui.resolve(e)
    return <Text>{JSON.stringify(e.props.body)}</Text>
  })
  on('session.info', () => ({ value: { transcriptPath: engine.transcriptPath ?? '' } }))
  on('fs.stat', () => ({ value: { kind: 'file', size: (engine.transcript ?? '').length } }))
  on('fs.read', () => ({ value: engine.transcript ?? '' }))
  on('session.id', () => ({ value: 'test-session' }))
  on('env.get', () => ({ value: '/nonexistent-config-dir' }))
  on('fs.write', (_: any, e: any) => {
    engine.written?.push(`${e.path} ${e.text.trim()}`)
    return { value: undefined }
  })
  on('process.run', (_: any, e: any) => {
    engine.written?.push(`run ${e.argv.join(' ')}`)
    return { value: { exitCode: 0, stdout: '', stderr: '' } }
  })
  return $.ui.mount({
    plugin: 'claude-sessions',
    surface: 'terminal',
    component: 'AgentRow',
    requestId: 'a1',
    props: {
      id: 'a1',
      status: 'running',
      kind: 'local_agent',
      type: 'general-purpose',
      description: 'Implement the retry logic',
      label: 'Implement the retry logic',
      model: 'claude-sonnet-5-5',
      tokenCount: 29000,
      contextWindowSize: 200000,
      isHidden: false,
      rowIds: ['a1'],
      ...props,
    },
  })
}

// The body as drawn: the raw text with its escape sequences, and the same without them.
const drawnRow = async (ui: any) => {
  const raw: string = JSON.parse((await ui.find({ type: 'Text' }))?.text ?? '""')
  await ui.unmount()
  return { raw, plain: raw.replace(SGR, '') }
}

test('a row shows type, name, model, effort, tokens with context percent, and the label', async ($, on) => {
  // 29000 tokens is 29.0k; 29000 / 200000 = 14.5%, floored to 14%.
  const { plain } = await drawnRow(await mountRow($, on, { name: 'builder', effort: 'high' }))
  expect(plain).toBe(
    '[general-purpose] builder · sonnet-5-5 · high · 29.0k (14%) · Implement the retry logic',
  )
})

test('fields the agent does not have are left out', async ($, on) => {
  // No name and no effort; line breaks in the label become spaces; 500 tokens is exact, 0.25% floors to 0%.
  const noName = await drawnRow(
    await mountRow($, on, { tokenCount: 500, label: 'Editing register.ts\nrunning tests' }),
  )
  expect(noName.plain).toBe('[general-purpose] · sonnet-5-5 · 500 (0%) · Editing register.ts running tests')
})

test('a row without a type starts with the name', async ($, on) => {
  const { plain } = await drawnRow(await mountRow($, on, { type: '', name: 'builder' }))
  expect(plain).toBe('builder · sonnet-5-5 · 29.0k (14%) · Implement the retry logic')
})

test('the model family sets the color, and any other model is yellow', async ($, on) => {
  // glm-5.3[1m] is shortened to glm-5.3 and is not an Anthropic family: SGR 33.
  const { raw } = await drawnRow(await mountRow($, on, { model: 'glm-5.3[1m]' }))
  expect(raw).toContain('\u001b[33mglm-5.3\u001b[0m')
})

test('a row leaves the subagent context size where cs ctx wait reads it', async ($, on) => {
  const written: string[] = []
  await (await mountRow($, on, { name: 'builder' }, { written })).unmount()
  const dir = '/nonexistent-config-dir/cs-ctx/test-session'
  // The file is written under a temporary name and renamed, so a reader never sees half of it.
  expect(written).toEqual([
    `${dir}/.a1.json.tmp {"name":"builder","status":"running","tokenCount":29000,"contextWindowSize":200000}`,
    `run mv -f ${dir}/.a1.json.tmp ${dir}/a1.json`,
  ])
})

test('an agent without a name is recorded with a null name, never its changing label', async ($, on) => {
  const written: string[] = []
  await (await mountRow($, on, {}, { written })).unmount()
  expect(written[0]).toContain('{"name":null,')
})

test('a model outside the Anthropic API takes its context size from the transcript', async ($, on) => {
  // Newest non-zero usage: 10 + 3000 + 500 = 3510 (the zero-usage record after it is skipped).
  const record = (usage: object) => JSON.stringify({ type: 'assistant', message: { usage } })
  const transcript = [
    'cut-in-half-line}',
    record({ input_tokens: 1, cache_read_input_tokens: 100, cache_creation_input_tokens: 0 }),
    record({ input_tokens: 10, cache_read_input_tokens: 3000, cache_creation_input_tokens: 500 }),
    record({ input_tokens: 0, cache_read_input_tokens: 0, cache_creation_input_tokens: 0 }),
  ].join('\n')
  const written: string[] = []
  await (
    await mountRow(
      $,
      on,
      { model: 'glm-5.3', tokenCount: 12 },
      { written, transcriptPath: '/t/session.jsonl', transcript },
    )
  ).unmount()
  expect(written[0]).toContain('"tokenCount":3510')
})
