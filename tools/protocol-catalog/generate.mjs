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
  configurationClientbound: ['custom_payload', 'disconnect', 'finish_configuration', 'keep_alive', 'ping', 'registry_data', 'select_known_packs', 'cookie_request', 'store_cookie', 'transfer', 'kick_disconnect', 'code_of_conduct', 'add_resource_pack', 'remove_resource_pack'],
  configurationServerbound: ['client_information', 'settings', 'custom_payload', 'finish_configuration', 'keep_alive', 'pong', 'select_known_packs', 'cookie_response', 'resource_pack_receive', 'accept_code_of_conduct'],
  playClientbound: ['login', 'keep_alive', 'kick_disconnect', 'position', 'chat', 'system_chat', 'player_chat', 'profileless_chat', 'update_health', 'death_combat_event', 'respawn', 'start_configuration', 'ping', 'add_resource_pack', 'remove_resource_pack'],
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
  versions.push({
    minecraftVersion: entry.minecraftVersion,
    protocolVersion: entry.version,
    schemaVersion: schema.schemaVersion,
    hasConfiguration: Boolean(data.protocol.configuration),
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
