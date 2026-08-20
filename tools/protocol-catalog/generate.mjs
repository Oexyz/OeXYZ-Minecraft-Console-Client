import { createRequire } from 'node:module'
import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const require = createRequire(import.meta.url)
const minecraftData = require('minecraft-data')
const packageInfo = require('minecraft-data/package.json')

const selectedNames = {
  loginClientbound: ['disconnect', 'encryption_begin', 'success', 'compress', 'login_plugin_request', 'cookie_request'],
  loginServerbound: ['login_start', 'encryption_begin', 'login_plugin_response', 'login_acknowledged', 'cookie_response'],
  configurationClientbound: ['custom_payload', 'disconnect', 'finish_configuration', 'keep_alive', 'ping', 'registry_data', 'select_known_packs', 'cookie_request', 'store_cookie', 'transfer', 'kick_disconnect', 'code_of_conduct', 'resource_pack_send', 'add_resource_pack', 'remove_resource_pack'],
  configurationServerbound: ['client_information', 'settings', 'custom_payload', 'finish_configuration', 'keep_alive', 'pong', 'select_known_packs', 'cookie_response', 'resource_pack_receive', 'accept_code_of_conduct'],
  playClientbound: ['login', 'keep_alive', 'kick_disconnect', 'position', 'chat', 'system_chat', 'player_chat', 'profileless_chat', 'update_health', 'death_combat_event', 'respawn', 'start_configuration', 'ping', 'player_info', 'player_info_update', 'player_info_remove', 'player_remove', 'resource_pack_send', 'add_resource_pack', 'remove_resource_pack'],
  playServerbound: ['keep_alive', 'teleport_confirm', 'settings', 'client_information', 'custom_payload', 'chat', 'chat_message', 'chat_command', 'chat_session_update', 'client_command', 'client_status', 'position', 'position_look', 'configuration_acknowledged', 'player_loaded', 'pong', 'resource_pack_receive']
}

function mappingFor (data, state, direction) {
  const packet = data?.protocol?.[state]?.[direction]?.types?.packet
  const fields = packet?.[1]
  const nameField = Array.isArray(fields) ? fields.find(field => field?.name === 'name') : null
  const mappings = nameField?.type?.[1]?.mappings ?? {}
  return Object.fromEntries(Object.entries(mappings).map(([id, name]) => [name, Number.parseInt(id, 16)]))
}

function subset (mapping, names) {
  const result = {}
  for (const name of names) if (Object.hasOwn(mapping, name)) result[name] = mapping[name]
  return result
}

function packetSchema (data, state, direction, packetName) {
  const localTypes = data?.protocol?.[state]?.[direction]?.types ?? {}
  const packet = localTypes.packet
  const fields = packet?.[1]
  const parameters = Array.isArray(fields) ? fields.find(field => field?.name === 'params') : null
  const typeName = parameters?.type?.[1]?.fields?.[packetName]
  if (!typeName) return null
  return localTypes[typeName] ?? data?.protocol?.types?.[typeName] ?? null
}

function containerFieldNames (schema) {
  if (!Array.isArray(schema) || schema[0] !== 'container' || !Array.isArray(schema[1])) return null
  return schema[1].map(field => field?.name)
}

function classifyResourcePackRequest (data, packetIds) {
  const layouts = new Set()
  for (const [state, ids] of [
    ['configuration', packetIds.configurationClientbound],
    ['play', packetIds.playClientbound]
  ]) {
    const packetName = Object.hasOwn(ids, 'add_resource_pack')
      ? 'add_resource_pack'
      : Object.hasOwn(ids, 'resource_pack_send') ? 'resource_pack_send' : null
    if (!packetName) continue
    const names = containerFieldNames(packetSchema(data, state, 'toClient', packetName))
    const signature = JSON.stringify(names)
    if (signature === JSON.stringify(['url', 'hash'])) layouts.add('UrlHash')
    else if (signature === JSON.stringify(['url', 'hash', 'forced', 'promptMessage'])) layouts.add('UrlHashForcedPrompt')
    else if (signature === JSON.stringify(['uuid', 'url', 'hash', 'forced', 'promptMessage'])) layouts.add('UuidUrlHashForcedPrompt')
    else throw new Error(`Unknown ${state} resource-pack request layout: ${signature}`)
  }
  if (layouts.size > 1) throw new Error(`Conflicting resource-pack request layouts: ${[...layouts].join(', ')}`)
  return [...layouts][0] ?? 'None'
}

function classifyResourcePackResponse (data, packetIds) {
  const layouts = new Set()
  for (const [state, ids] of [
    ['configuration', packetIds.configurationServerbound],
    ['play', packetIds.playServerbound]
  ]) {
    if (!Object.hasOwn(ids, 'resource_pack_receive')) continue
    const names = containerFieldNames(packetSchema(data, state, 'toServer', 'resource_pack_receive'))
    const signature = JSON.stringify(names)
    if (signature === JSON.stringify(['hash', 'result'])) layouts.add('HashAndStatus')
    else if (signature === JSON.stringify(['result'])) layouts.add('StatusOnly')
    else if (signature === JSON.stringify(['uuid', 'result'])) layouts.add('UuidAndStatus')
    else throw new Error(`Unknown ${state} resource-pack response layout: ${signature}`)
  }
  if (layouts.size > 1) throw new Error(`Conflicting resource-pack response layouts: ${[...layouts].join(', ')}`)
  return [...layouts][0] ?? 'None'
}

function schemaFor (entry) {
  const candidates = [entry.minecraftVersion, entry.majorVersion]
  if (entry.minecraftVersion === '26.2') candidates.push('26.1')
  for (const candidate of candidates) {
    const data = minecraftData(candidate)
    if (data?.protocol) return { data, schemaVersion: candidate }
  }
  return null
}

const releases = minecraftData.versions.pc
  .filter(entry => entry.usesNetty && entry.version >= 47 && /^\d+(?:\.\d+){1,2}$/.test(entry.minecraftVersion))
  .filter((entry, index, values) => values.findIndex(other => other.minecraftVersion === entry.minecraftVersion && other.version === entry.version) === index)
  .sort((a, b) => a.version - b.version || a.minecraftVersion.localeCompare(b.minecraftVersion, undefined, { numeric: true }))

const versions = []
for (const entry of releases) {
  const schema = schemaFor(entry)
  if (!schema) continue
  const data = schema.data
  const packetIds = {
    loginClientbound: subset(mappingFor(data, 'login', 'toClient'), selectedNames.loginClientbound),
    loginServerbound: subset(mappingFor(data, 'login', 'toServer'), selectedNames.loginServerbound),
    configurationClientbound: subset(mappingFor(data, 'configuration', 'toClient'), selectedNames.configurationClientbound),
    configurationServerbound: subset(mappingFor(data, 'configuration', 'toServer'), selectedNames.configurationServerbound),
    playClientbound: subset(mappingFor(data, 'play', 'toClient'), selectedNames.playClientbound),
    playServerbound: subset(mappingFor(data, 'play', 'toServer'), selectedNames.playServerbound)
  }
  const resourcePackRequestLayout = classifyResourcePackRequest(data, packetIds)
  const resourcePackResponseLayout = classifyResourcePackResponse(data, packetIds)
  if (resourcePackRequestLayout !== 'None' && resourcePackResponseLayout === 'None') {
    throw new Error(`Minecraft ${entry.minecraftVersion} has a resource-pack request but no response packet`)
  }
  versions.push({
    minecraftVersion: entry.minecraftVersion,
    protocolVersion: entry.version,
    schemaVersion: schema.schemaVersion,
    hasConfiguration: Boolean(data.protocol.configuration),
    resourcePackRequestLayout,
    resourcePackResponseLayout,
    packetIds
  })
}

const catalog = {
  formatVersion: 1,
  source: {
    name: 'PrismarineJS minecraft-data',
    packageVersion: packageInfo.version,
    license: 'MIT',
    repository: 'https://github.com/PrismarineJS/minecraft-data'
  },
  supportedRange: { minimum: '1.8', maximum: '26.2' },
  versions
}

const here = dirname(fileURLToPath(import.meta.url))
const output = resolve(here, '../../src/OeXYZ.Protocol/Resources/protocol-catalog.json')
mkdirSync(dirname(output), { recursive: true })
writeFileSync(output, JSON.stringify(catalog, null, 2) + '\n')
console.log(`Generated ${versions.length} release mappings at ${output}`)

// 26.2 reuses the 26.1 data schema in the pinned minecraft-data release.
// The English catalog is embedded at runtime so translatable chat components
// never expose raw keys such as entity.minecraft.slime to end users.
const languageSchemaVersion = '26.1'
const language = minecraftData(languageSchemaVersion)?.language
if (!language) throw new Error(`No language catalog is available for ${languageSchemaVersion}`)
const languageOutput = resolve(here, '../../src/OeXYZ.Protocol/Resources/en-us.json')
writeFileSync(languageOutput, JSON.stringify(language, null, 2) + '\n')
console.log(`Generated ${Object.keys(language).length} English translations at ${languageOutput}`)
