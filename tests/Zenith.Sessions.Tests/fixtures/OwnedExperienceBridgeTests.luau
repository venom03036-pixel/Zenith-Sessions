-- The verifier prepends the actual server Script as install(game, Instance, os, task, warn).
local universe = 8000000001
local sourcePlace, destinationPlace = 9000000001, 9000000002
local user = 8000000002
local sourceJob = "12345678-1234-1234-1234-123456789abc"
local destinationJob = "23456789-2345-2345-2345-23456789abcd"
local operation = "34567890-3456-3456-3456-34567890abcd"
local otherOperation = "45678901-4567-4567-4567-45678901abcd"
local recordKey = "u8000000002"
local passed = 0

local function check(name, work)
    work()
    passed = passed + 1
    print("PASS " .. name)
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
        function players:GetPlayerByUserId(id)
            return id == user and player.Parent == players and player or nil
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
                JSONDecode = function(_, _) return state.command end,
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
    function state:send(changes, status)
        self.command = {
            version = 1, operationId = operation, universeId = universe, userId = user,
            sourcePlaceId = sourcePlace, sourceJobId = sourceJob,
            destinationPlaceId = not status and destinationPlace or nil,
            destinationJobId = not status and destinationJob or nil,
            issuedUtc = self.now, expiresUtc = self.now + 45,
        }
        for key, value in pairs(changes or {}) do
            self.command[key] = value
        end
        self.source.message({ Data = "fixture command" })
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
    state.now = state.now + 45
    state:arrive(state.teleport.options.data)
    assert(state.receipts[recordKey].stage == "received")
end)

check("storage failure prevents a move and frees the owner", function()
    local state = fixture()
    state.failWrite = true
    state:send()
    assert(state.teleports == 0 and state.receipts[recordKey] == nil)
    state.failWrite = false
    state:send()
    assert(state.teleports == 1)
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

print(string.format("Passed %d owned-experience server checks.", passed))
