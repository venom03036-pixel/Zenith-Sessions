-- The verifier prepends the actual server Script as install(game, Instance, os, task, warn).
local universe = 8000000001
local sourcePlace, destinationPlace = 9000000001, 9000000002
local user = 8000000002
local sourceJob = "12345678-1234-1234-1234-123456789abc"
local destinationJob = "23456789-2345-2345-2345-23456789abcd"
local operation = "34567890-3456-3456-3456-34567890abcd"
local otherOperation = "45678901-4567-4567-4567-45678901abcd"
local recordKey = "u8000000002"
-- The exact text RobloxOwnedExperienceBridge publishes for this command, issued at 2026-11-07T10:00Z.
-- OwnedExperienceBridgeTests.cs reads both lines from this file, so the two suites pin one payload.
local goldenIssued = 1794045600
local goldenMove = [[{"version":1,"operationId":"34567890-3456-3456-3456-34567890abcd","universeId":8000000001,"userId":8000000002,"sourcePlaceId":9000000001,"sourceJobId":"12345678-1234-1234-1234-123456789abc","destinationPlaceId":9000000002,"destinationJobId":"23456789-2345-2345-2345-23456789abcd","issuedUtc":1794045600,"expiresUtc":1794045645}]]
local goldenStatus = [[{"version":1,"operationId":"34567890-3456-3456-3456-34567890abcd","universeId":8000000001,"userId":8000000002,"sourcePlaceId":9000000001,"sourceJobId":"12345678-1234-1234-1234-123456789abc","destinationPlaceId":null,"destinationJobId":null,"issuedUtc":1794045600,"expiresUtc":1794045645}]]
-- The receipts the script stores for those two commands at goldenIssued, as JSON with sorted keys: the move's
-- arrival receipt from the destination and the status receipt from the source. claimId is the fixture's first
-- generated claim, and the status command has no destination keys because Roblox stores no nil fields.
-- OwnedExperienceBridgeTests.cs requires RobloxOwnedExperienceBridge to acknowledge both texts.
local goldenArrivedReceipt = [[{"claimId":"87654321-8765-8765-8765-000000000001","command":{"destinationJobId":"23456789-2345-2345-2345-23456789abcd","destinationPlaceId":9000000002,"expiresUtc":1794045645,"issuedUtc":1794045600,"operationId":"34567890-3456-3456-3456-34567890abcd","sourceJobId":"12345678-1234-1234-1234-123456789abc","sourcePlaceId":9000000001,"universeId":8000000001,"userId":8000000002,"version":1},"observedJobId":"23456789-2345-2345-2345-23456789abcd","observedPlaceId":9000000002,"observedUtc":1794045600,"stage":"arrived"}]]
local goldenStatusReceipt = [[{"claimId":"87654321-8765-8765-8765-000000000001","command":{"expiresUtc":1794045645,"issuedUtc":1794045600,"operationId":"34567890-3456-3456-3456-34567890abcd","sourceJobId":"12345678-1234-1234-1234-123456789abc","sourcePlaceId":9000000001,"universeId":8000000001,"userId":8000000002,"version":1},"observedJobId":"12345678-1234-1234-1234-123456789abc","observedPlaceId":9000000001,"observedUtc":1794045600,"stage":"atSource"}]]
local passed = 0

local function check(name, work)
    work()
    passed = passed + 1
    print("PASS " .. name)
end

-- A server warning names the step and the reason only, never a user, universe, place, job, operation or claim ID.
local function assertWithoutIds(warning, extra)
    local ids = { tostring(user), tostring(universe), tostring(sourcePlace), tostring(destinationPlace),
        sourceJob, destinationJob, operation, otherOperation, "87654321-" }
    for _, id in ipairs(extra or {}) do
        table.insert(ids, id)
    end
    for _, id in ipairs(ids) do
        assert(not string.find(warning, id, 1, true), "the warning exposes an ID (" .. id .. "): " .. warning)
    end
end

-- A model of HttpService:JSONDecode for the flat objects the bridge publishes: strings without escapes,
-- integers, true, false and null. Roblox drops null members, so they decode to nil. Anything else raises
-- an error, as Roblox does for malformed JSON. It is stricter than Roblox, never looser.
local function decodeFlatObject(text)
    local position = 1
    local function skip()
        position = string.find(text, "[^ \t\r\n]", position) or #text + 1
    end
    local function take(token)
        skip()
        assert(string.sub(text, position, position + #token - 1) == token, "malformed JSON")
        position = position + #token
    end
    local function readString()
        take('"')
        local finish = string.find(text, '"', position, true)
        assert(finish ~= nil, "unterminated JSON string")
        local value = string.sub(text, position, finish - 1)
        assert(not string.find(value, "[\\%c]"), "unsupported JSON string")
        position = finish + 1
        return value
    end
    local result = {}
    take("{")
    skip()
    if string.sub(text, position, position) == "}" then
        position = position + 1
    else
        repeat
            local name = readString()
            take(":")
            skip()
            local value
            local digits = string.match(text, "^-?%d+", position)
            if string.sub(text, position, position) == '"' then
                value = readString()
            elseif digits then
                position = position + #digits
                assert(not string.match(text, "^[%.eE]", position), "only integers are expected")
                value = tonumber(digits)
            else
                local word = string.match(text, "^%a+", position) or ""
                position = position + #word
                if word == "true" or word == "false" then
                    value = word == "true"
                else
                    assert(word == "null", "unsupported JSON value")
                end
            end
            result[name] = value
            skip()
            local separator = string.sub(text, position, position)
            position = position + 1
            assert(separator == "," or separator == "}", "malformed JSON")
        until separator == "}"
    end
    skip()
    assert(position > #text, "trailing JSON text")
    return result
end

local function encodeScalar(value)
    if value == nil then
        return "null"
    elseif type(value) == "number" then
        assert(value % 1 == 0, "the fixture writes only integers")
        return string.format("%.0f", value)
    elseif type(value) == "boolean" then
        return tostring(value)
    end
    assert(type(value) == "string" and not string.find(value, '["\\%c]'), "unsupported fixture value")
    return '"' .. value .. '"'
end

-- Writes a stored receipt as JSON text with its keys sorted at every level, the form of the receipt goldens.
local function encodeSorted(value)
    if type(value) ~= "table" then
        return encodeScalar(value)
    end
    local names = {}
    for name in pairs(value) do
        assert(type(name) == "string", "a receipt has only named fields")
        table.insert(names, name)
    end
    table.sort(names)
    local parts = {}
    for _, name in ipairs(names) do
        table.insert(parts, '"' .. name .. '":' .. encodeSorted(value[name]))
    end
    return "{" .. table.concat(parts, ",") .. "}"
end

-- Writes a command as JSON text in RobloxOwnedExperienceBridge.WriteCommand's field order, with a nil
-- field written as null and any extra fixture fields after it.
local commandFields = { "version", "operationId", "universeId", "userId", "sourcePlaceId", "sourceJobId",
    "destinationPlaceId", "destinationJobId", "issuedUtc", "expiresUtc" }
local function encodeCommand(command)
    local names = table.clone(commandFields)
    local extras = {}
    for name in pairs(command) do
        if not table.find(commandFields, name) then
            table.insert(extras, name)
        end
    end
    table.sort(extras)
    for _, name in ipairs(extras) do
        table.insert(names, name)
    end
    local parts = {}
    for _, name in ipairs(names) do
        table.insert(parts, '"' .. name .. '":' .. encodeScalar(command[name]))
    end
    return "{" .. table.concat(parts, ",") .. "}"
end

local function fixture()
    local state = {
        now = 2000000000, receipts = {}, teleports = 0, claims = 0, updates = 0,
        subscribeFailures = 0, waits = {}, warnings = {},
    }

    local function makeServer(placeId, jobId, privateId, studio, installScript)
        local result = {}
        local players = {}
        local player = { UserId = user, Parent = players, join = {} }
        function player:GetJoinData()
            return self.join
        end
        -- Extra players for the capacity case; the runner allowlists 8000000011 to 8000000014 as well.
        local others = {}
        function players:GetPlayerByUserId(id)
            local found = id == user and player or others[id]
            return found ~= nil and found.Parent == players and found or nil
        end
        function result.addPlayer(id)
            others[id] = { UserId = id, Parent = players, join = {} }
        end
        players.PlayerAdded = { Connect = function(_, handler) result.arrive = handler end }
        result.player = player
        local store = {}
        function store:UpdateAsync(key, transform)
            state.updates = state.updates + 1
            local changed = transform(state.receipts[key])
            if state.failWrite then
                error("storage unavailable")
            end
            if changed ~= nil then
                state.receipts[key] = changed
            end
            return changed
        end
        local services = {
            DataStoreService = { GetDataStore = function(_, name, scope)
                assert(name == "ZenithSessionsReceiptsV1" and scope == "global")
                return store
            end },
            HttpService = {
                JSONDecode = function(_, text) return decodeFlatObject(text) end,
                GenerateGUID = function(_, _)
                    state.claims = state.claims + 1
                    return string.format("87654321-8765-8765-8765-%012d", state.claims)
                end,
            },
            MessagingService = { SubscribeAsync = function(_, topic, handler)
                assert(topic == "ZenithSessionsV1")
                if state.subscribeFailures > 0 then
                    state.subscribeFailures = state.subscribeFailures - 1
                    error("MessagingService is unavailable")
                end
                result.message = handler
            end },
            Players = players,
            RunService = { IsStudio = function() return studio == true end },
            TeleportService = {
                TeleportInitFailed = { Connect = function(_, handler) result.fail = handler end },
                TeleportAsync = function(_, place, targets, options)
                    state.teleports = state.teleports + 1
                    state.teleport = { place = place, player = targets[1], count = #targets, options = options }
                    if state.onTeleport then
                        state.onTeleport(place, targets, options)
                    end
                end,
            },
        }
        local game = { GameId = universe, PlaceId = placeId, JobId = jobId, PrivateServerId = privateId or "" }
        function game:GetService(name) return services[name] end
        local instance = { new = function(kind)
            assert(kind == "TeleportOptions")
            return {
                SetTeleportData = function(self, data) self.data = data end,
                GetTeleportData = function(self) return self.data end,
            }
        end }
        local fakeClock = { time = function() return state.now end }
        -- Roblox's task scheduler and server log: record the requested sleep and message instead of waiting.
        local fakeTask = { wait = function(seconds) table.insert(state.waits, seconds) end }
        local function fakeWarn(text) table.insert(state.warnings, text) end
        (installScript or install)(game, instance, fakeClock, fakeTask, fakeWarn)
        return result
    end
    state.source = makeServer(sourcePlace, sourceJob)
    state.destination = makeServer(destinationPlace, destinationJob)
    state.makeServer = makeServer
    -- Every command reaches the script as JSON text, through its size cap and JSONDecode.
    function state:publish(text)
        self.source.message({ Data = text })
    end
    function state:send(changes, status)
        local command = {
            version = 1, operationId = operation, universeId = universe, userId = user,
            sourcePlaceId = sourcePlace, sourceJobId = sourceJob,
            destinationPlaceId = not status and destinationPlace or nil,
            destinationJobId = not status and destinationJob or nil,
            issuedUtc = self.now, expiresUtc = self.now + 45,
        }
        for key, value in pairs(changes or {}) do
            command[key] = value
        end
        self:publish(encodeCommand(command))
    end
    function state:arrive(data, fromPlace, fromUniverse)
        local player = self.destination.player
        player.join = { TeleportData = data, SourcePlaceId = fromPlace or sourcePlace, SourceGameId = fromUniverse or universe }
        self.destination.arrive(player)
    end
    return state
end

check("shipped defaults, Studio and private servers stay idle", function()
    local state = fixture()
    assert(state.makeServer(sourcePlace, sourceJob, nil, false, installDisabled).message == nil)
    assert(state.makeServer(sourcePlace, sourceJob, nil, true).message == nil)
    assert(state.makeServer(sourcePlace, sourceJob, "private-server-id").message == nil)
end)

check("status records only current player presence", function()
    local state = fixture()
    state:send({ ignored = "extra field" }, true)
    local record = state.receipts[recordKey]
    assert(record.stage == "atSource" and record.command.userId == user and record.observedJobId == sourceJob)
    assert(record.command.ignored == nil and state.teleports == 0)
end)

check("invalid, expired, wrong-source and unapproved commands do no work", function()
    local state = fixture()
    for _, changes in ipairs({
        { userId = user + 1 },
        { universeId = universe + 1 },
        { sourcePlaceId = destinationPlace },
        { sourceJobId = destinationJob },
        { destinationPlaceId = destinationPlace + 1 },
        { expiresUtc = state.now - 1 },
        { operationId = "invalid" },
    }) do
        state:send(changes)
    end
    assert(state.updates == 0 and state.teleports == 0 and state.receipts[recordKey] == nil)
    state.source.player.Parent = nil
    state:send()
    assert(state.updates == 0 and state.teleports == 0)
end)

-- The Windows transport reports ClockSkew once the PC clock is more than two seconds off Roblox's. Keep both in step.
check("commands outside the server clock allowance do no work", function()
    local state = fixture()
    -- A PC three seconds ahead of the server, and one a whole lifetime behind it.
    state:send({ issuedUtc = state.now + 3, expiresUtc = state.now + 48 })
    state:send({ issuedUtc = state.now - 45, expiresUtc = state.now })
    assert(state.updates == 0 and state.teleports == 0 and state.receipts[recordKey] == nil,
        "the script accepted a command outside its clock allowance")
    -- Two seconds ahead is still inside the allowance, so the limit itself is what stopped the first command.
    state:send({ issuedUtc = state.now + 2, expiresUtc = state.now + 47 })
    assert(state.teleports == 1 and state.receipts[recordKey].stage == "teleportInitiated",
        "the script refused a command issued two seconds ahead of its clock")
end)

check("exact public move separates initiation and arrival", function()
    local state = fixture()
    state:send()
    local move = state.teleport
    assert(state.teleports == 1 and move.place == destinationPlace and move.options.ServerInstanceId == destinationJob)
    assert(move.player == state.source.player and move.count == 1)
    assert(state.receipts[recordKey].stage == "teleportInitiated")
    state:arrive(move.options.data)
    local record = state.receipts[recordKey]
    assert(record.stage == "arrived" and record.observedPlaceId == destinationPlace and record.observedJobId == destinationJob)
    -- A clean move leaves the creator's server output quiet.
    assert(#state.warnings == 0, "a clean move wrote " .. #state.warnings .. " server warnings")
end)

check("duplicate and competing commands cannot repeat a move", function()
    local state = fixture()
    state:send()
    state:send()
    state:send({ operationId = otherOperation }, true)
    assert(state.teleports == 1 and state.receipts[recordKey].command.operationId == operation)
    state.now = state.now + 46
    state:send({ operationId = otherOperation }, true)
    assert(state.receipts[recordKey].command.operationId == otherOperation and state.teleports == 1)
end)

-- "Confirm the account is at the source, then move it" must work inside one status check's 45 s.
check("a completed status check does not hold the slot against a later move or status", function()
    local lateStatus = "56789012-5678-5678-5678-56789012abcd"
    local state = fixture()
    state:send(nil, true)
    assert(state.receipts[recordKey].stage == "atSource" and state.teleports == 0)
    state.now = state.now + 10
    state:send({ operationId = otherOperation })
    local record = state.receipts[recordKey]
    assert(state.teleports == 1, "a finished status check blocked a move sent 10 s later")
    assert(record.command.operationId == otherOperation and record.stage == "teleportInitiated")
    -- The move still holds the slot for its own 45 s, so a status check cannot replace it.
    state.now = state.now + 10
    state:send({ operationId = lateStatus }, true)
    assert(state.receipts[recordKey].command.operationId == otherOperation and state.teleports == 1,
        "a status check replaced a live move receipt")

    -- A newer status check also replaces a finished one.
    local statuses = fixture()
    statuses:send(nil, true)
    statuses.now = statuses.now + 10
    statuses:send({ operationId = lateStatus }, true)
    record = statuses.receipts[recordKey]
    assert(record.stage == "atSource" and record.command.operationId == lateStatus,
        "a finished status check blocked a newer status check")
    -- An out-of-order command, even a move, still cannot replace it.
    statuses:send({ operationId = otherOperation, issuedUtc = statuses.now - 5, expiresUtc = statuses.now + 40 })
    assert(statuses.teleports == 0 and statuses.receipts[recordKey].command.operationId == lateStatus,
        "a move issued before the newer status check replaced it")
end)

check("mismatched receipt claims and wrong places cannot confirm arrival", function()
    local state = fixture()
    state:send()
    local move = state.teleport
    state:arrive({ zenithOperationId = operation, zenithClaimId = otherOperation })
    state:arrive(move.options.data, destinationPlace)
    state:arrive(move.options.data, sourcePlace, universe + 1)
    local wrongServer = state.makeServer(destinationPlace, "01234567-0123-0123-0123-0123456789ab")
    wrongServer.player.join = { TeleportData = move.options.data, SourcePlaceId = sourcePlace, SourceGameId = universe }
    wrongServer.arrive(wrongServer.player)
    assert(state.receipts[recordKey].stage == "teleportInitiated")
end)

check("destination arrival wins even if source acknowledgement is late", function()
    local state = fixture()
    state.onTeleport = function(_, _, options) state:arrive(options.data) end
    state:send()
    assert(state.receipts[recordKey].stage == "arrived")
end)

check("definite source failure during initiation gets a terminal receipt", function()
    local state = fixture()
    state.onTeleport = function(place, targets, options)
        state.source.fail(targets[1], nil, nil, place, options)
    end
    state:send()
    assert(state.receipts[recordKey].stage == "rejected" and state.teleports == 1)
    state:arrive(state.teleport.options.data)
    assert(state.receipts[recordKey].stage == "rejected")
end)

check("ambiguous errors stay unknown and expired arrivals are ignored", function()
    local state = fixture()
    state.onTeleport = function() error("ambiguous failure") end
    state:send()
    assert(state.receipts[recordKey].stage == "received" and state.teleports == 1)
    -- The move is not replayed, but the creator's server output names why it stopped, without command IDs.
    assert(#state.warnings == 1, "a failed teleport wrote " .. #state.warnings .. " server warnings instead of 1")
    local warning = state.warnings[1]
    assert(string.find(warning, "ambiguous failure", 1, true), "the warning does not carry the Roblox error: " .. warning)
    assert(not string.find(warning, operation, 1, true) and not string.find(warning, "87654321-", 1, true),
        "the warning exposes an operation or claim ID: " .. warning)
    state.now = state.now + 45
    state:arrive(state.teleport.options.data)
    assert(state.receipts[recordKey].stage == "received" and #state.warnings == 1)
end)

check("storage failure prevents a move and frees the owner", function()
    local state = fixture()
    state.failWrite = true
    state:send()
    assert(state.teleports == 0 and state.receipts[recordKey] == nil)
    assert(#state.warnings == 1, "a failed receipt write wrote " .. #state.warnings .. " server warnings instead of 1")
    assert(string.find(state.warnings[1], "storage unavailable", 1, true),
        "the warning does not carry the Roblox error: " .. state.warnings[1])
    state.failWrite = false
    state:send()
    assert(state.teleports == 1 and #state.warnings == 1)
end)

check("a failed deferred terminal receipt is reported and frees the owner", function()
    local state = fixture()
    state.onTeleport = function(place, targets, options)
        -- The failure callback is deferred behind the running move; then both receipt writes fail.
        state.source.fail(targets[1], nil, nil, place, options)
        state.failWrite = true
    end
    state:send()
    assert(state.teleports == 1 and state.receipts[recordKey].stage == "received")
    assert(#state.warnings == 2, "two failed steps wrote " .. #state.warnings .. " server warnings instead of 2")
    for _, warning in ipairs(state.warnings) do
        assert(string.find(warning, "storage unavailable", 1, true), "the warning does not carry the Roblox error: " .. warning)
    end
    -- Each warning names its own step, so the creator can tell the move from the deferred receipt behind it.
    assert(string.find(state.warnings[1], "bridge move step stopped", 1, true),
        "the first warning does not name the move step: " .. state.warnings[1])
    assert(string.find(state.warnings[2], "bridge teleport failure receipt step stopped", 1, true),
        "the second warning does not name the deferred teleport failure receipt: " .. state.warnings[2])
    state.failWrite = false
    state.onTeleport = nil
    state.now = state.now + 46
    state:send({ operationId = otherOperation }, true)
    assert(state.receipts[recordKey].stage == "atSource" and state.receipts[recordKey].command.operationId == otherOperation)
end)

-- A command the script ignores because the player or the server is busy is named in the server log, so a failed
-- live trial can be told apart from a command that never arrived. The app still shows it as pending, then expired.
check("a command for a player with one still running is reported once without IDs", function()
    local state = fixture()
    state.onTeleport = function()
        state:send({ operationId = otherOperation })
    end
    state:send()
    assert(state.teleports == 1 and state.receipts[recordKey].command.operationId == operation
        and state.receipts[recordKey].stage == "teleportInitiated")
    assert(#state.warnings == 1,
        "a command ignored while the player was busy wrote " .. #state.warnings .. " server warnings instead of 1")
    assert(string.find(state.warnings[1], "ignored this move because a command for this player is still running", 1, true),
        "the warning does not say the player was busy: " .. tostring(state.warnings[1]))
    assertWithoutIds(state.warnings[1])
end)

check("a second waiting result for a player is reported once without IDs", function()
    local state = fixture()
    state.onTeleport = function(place, targets, options)
        -- Both failure callbacks arrive while the move runs. The first waits behind it; there is room for one only.
        state.source.fail(targets[1], nil, nil, place, options)
        state.source.fail(targets[1], nil, nil, place, options)
    end
    state:send()
    assert(state.teleports == 1 and state.receipts[recordKey].stage == "rejected")
    assert(#state.warnings == 1,
        "a second waiting result wrote " .. #state.warnings .. " server warnings instead of 1")
    assert(string.find(state.warnings[1],
        "ignored this teleport failure receipt because another result for this player is already waiting", 1, true),
        "the warning does not say a result was already waiting: " .. tostring(state.warnings[1]))
    assertWithoutIds(state.warnings[1])
end)

check("a command over the server's limit of four is reported once without IDs", function()
    local state = fixture()
    -- Four more allowlisted players. Each move sends the next one from inside its teleport, so four run at once.
    local crowd = { 8000000011, 8000000012, 8000000013, 8000000014 }
    for _, id in ipairs(crowd) do
        state.source.addPlayer(id)
    end
    local nextIndex = 1
    state.onTeleport = function()
        local id = crowd[nextIndex]
        nextIndex = nextIndex + 1
        if id then
            state:send({ userId = id })
        end
    end
    state:send()
    assert(state.teleports == 4 and state.receipts["u8000000013"].stage == "teleportInitiated"
        and state.receipts["u8000000014"] == nil, "the fifth command was not the one left out")
    assert(#state.warnings == 1,
        "a command over the server limit wrote " .. #state.warnings .. " server warnings instead of 1")
    assert(string.find(state.warnings[1], "ignored this move because this server is already running 4 bridge commands", 1, true),
        "the warning does not say the server was full: " .. tostring(state.warnings[1]))
    assertWithoutIds(state.warnings[1], { "8000000011", "8000000012", "8000000013", "8000000014" })
    -- The ignored command took no slot, so the same command runs once the others have finished.
    state.onTeleport = nil
    state:send({ userId = crowd[4] })
    assert(state.teleports == 5 and state.receipts["u8000000014"].stage == "teleportInitiated" and #state.warnings == 1)
end)

check("a failed subscription is retried with capped backoff and later commands are received", function()
    local state = fixture()
    assert(#state.waits == 0 and #state.warnings == 0)
    state.subscribeFailures = 8
    local server = state.makeServer(sourcePlace, sourceJob)
    assert(server.message ~= nil and state.subscribeFailures == 0)
    local expected = { 1, 2, 4, 8, 16, 32, 60, 60 }
    assert(#state.waits == #expected and #state.warnings == #expected)
    for index, seconds in ipairs(expected) do
        assert(state.waits[index] == seconds)
        assert(string.find(state.warnings[index], "Retrying in " .. seconds .. " s.", 1, true))
    end
    state.source = server
    state:send(nil, true)
    assert(state.receipts[recordKey].stage == "atSource" and state.receipts[recordKey].observedJobId == sourceJob)
end)

check("the C# move and status payloads are decoded and accepted", function()
    local state = fixture()
    state.now = goldenIssued
    state:publish(goldenMove)
    local record = state.receipts[recordKey]
    assert(record ~= nil and record.stage == "teleportInitiated" and state.teleports == 1,
        "the script did not accept the C# move payload")
    assert(record.command.operationId == operation and record.command.destinationJobId == destinationJob
        and record.command.issuedUtc == goldenIssued and record.command.expiresUtc == goldenIssued + 45)
    local status = fixture()
    status.now = goldenIssued
    status:publish(goldenStatus)
    record = status.receipts[recordKey]
    assert(record ~= nil and record.stage == "atSource", "the script did not accept the C# status payload")
    assert(record.command.destinationPlaceId == nil and record.command.destinationJobId == nil and status.teleports == 0)
end)

check("the receipts stored for the C# payloads match the receipt goldens the C# reader acknowledges", function()
    local move = fixture()
    move.now = goldenIssued
    move:publish(goldenMove)
    move:arrive(move.teleport.options.data)
    local stored = encodeSorted(move.receipts[recordKey])
    assert(stored == goldenArrivedReceipt, "the arrival receipt differs from goldenArrivedReceipt: " .. stored)
    local status = fixture()
    status.now = goldenIssued
    status:publish(goldenStatus)
    stored = encodeSorted(status.receipts[recordKey])
    assert(stored == goldenStatusReceipt, "the status receipt differs from goldenStatusReceipt: " .. stored)
end)

check("payloads over 1024 bytes are ignored", function()
    local state = fixture()
    state.now = goldenIssued
    state:publish(goldenMove .. string.rep(" ", 1025 - #goldenMove))
    assert(state.updates == 0 and state.teleports == 0, "the script processed a 1025-byte payload")
    -- The same command padded to exactly 1024 bytes is still accepted, so the size cap is what stopped it.
    state:publish(goldenMove .. string.rep(" ", 1024 - #goldenMove))
    assert(state.teleports == 1 and state.receipts[recordKey].stage == "teleportInitiated",
        "the script did not accept a 1024-byte payload")
end)

print(string.format("Passed %d owned-experience server checks.", passed))
