-- Install as a server Script in ServerScriptService of each approved place.
-- Set these values in the creator's private project before publishing.
local universeId = 0
local allowedPlaces = {}
local allowedUsers = {}

local DataStoreService = game:GetService("DataStoreService")
local HttpService = game:GetService("HttpService")
local MessagingService = game:GetService("MessagingService")
local Players = game:GetService("Players")
local RunService = game:GetService("RunService")
local TeleportService = game:GetService("TeleportService")

if RunService:IsStudio() or universeId == 0 or game.GameId ~= universeId
    or not allowedPlaces[game.PlaceId] or game.PrivateServerId ~= "" or game.JobId == "" then
    return
end

local jobId = string.lower(game.JobId)
local store = DataStoreService:GetDataStore("ZenithSessionsReceiptsV1", "global")
local busyUsers = {}
local active = 0
local maximumActive = 4

local function isGuid(value)
    if type(value) ~= "string" or #value ~= 36 or value ~= string.lower(value)
        or value == "00000000-0000-0000-0000-000000000000" then
        return false
    end
    local a, b, c, d, e = string.match(value, "^(%x+)-(%x+)-(%x+)-(%x+)-(%x+)$")
    return a ~= nil and #a == 8 and #b == 4 and #c == 4 and #d == 4 and #e == 12
end

local function fresh(command, now)
    local issued = command.issuedUtc
    return type(issued) == "number" and issued % 1 == 0 and issued > 0 and issued <= now + 2
        and command.expiresUtc == issued + 45 and now < command.expiresUtc
end

local function valid(command, now)
    if type(command) ~= "table" or command.version ~= 1 or command.universeId ~= universeId
        or type(command.userId) ~= "number" or not allowedUsers[command.userId]
        or not allowedPlaces[command.sourcePlaceId] or not isGuid(command.sourceJobId)
        or not isGuid(command.operationId) or not fresh(command, now) then
        return false
    end
    if command.destinationPlaceId == nil then
        return command.destinationJobId == nil
    end
    return allowedPlaces[command.destinationPlaceId] == true and isGuid(command.destinationJobId)
        and command.destinationJobId ~= command.sourceJobId
end

local function key(userId)
    return string.format("u%.0f", userId)
end

local function receipt(command, stage, claimId, now)
    return {
        command = command,
        stage = stage,
        claimId = claimId,
        observedPlaceId = game.PlaceId,
        observedJobId = jobId,
        observedUtc = now,
    }
end

local function claimed(previous, commandId, claimId)
    return type(previous) == "table" and type(previous.command) == "table"
        and previous.command.operationId == commandId and previous.claimId == claimId
end

local function updateStage(userId, commandId, claimId, stage, accept)
    store:UpdateAsync(key(userId), function(previous)
        local now = os.time()
        if not claimed(previous, commandId, claimId) or not valid(previous.command, now)
            or previous.command.userId ~= userId or not accept(previous) then
            return nil
        end
        return receipt(previous.command, stage, claimId, now)
    end)
end

-- Every warning names a fixed step ("move", "status check", "arrival receipt" or "teleport failure receipt") and
-- the reason. The script adds no user, place, job, operation or claim ID and no command contents; a failed step
-- adds only Roblox's own error text.
local unconfirmed = " The app shows this command as unconfirmed until it expires."

-- A throttled or failed Roblox call leaves its outcome unknown. Never replay a move here, but write the
-- Roblox error to the server log so the creator can tell a throttle from a teleport failure or a script bug.
local function attempt(step, work)
    local completed, failure = pcall(work)
    if not completed then
        warn(string.format("Zenith Sessions bridge %s step stopped and was not retried (%s).", step, tostring(failure))
            .. unconfirmed)
    end
end

-- A busy player or a full server leaves a request without a receipt. Say so in the server log, so a failed
-- trial can be told apart from a command that never arrived.
local function ignored(step, reason)
    warn(string.format("Zenith Sessions bridge ignored this %s because %s.", step, reason) .. unconfirmed)
end

local function bounded(userId, step, work, terminal)
    local running = busyUsers[userId]
    if running then
        if not terminal then
            ignored(step, "a command for this player is still running")
        elseif running.next then
            ignored(step, "another result for this player is already waiting")
        else
            running.next = work
            running.nextStep = step
        end
        return
    end
    if active >= maximumActive then
        ignored(step, string.format("this server is already running %d bridge commands", maximumActive))
        return
    end
    local owner = {}
    busyUsers[userId] = owner
    active = active + 1
    attempt(step, work)
    if owner.next then
        attempt(owner.nextStep, owner.next)
    end
    active = active - 1
    busyUsers[userId] = nil
end

local function onMessage(message)
    if type(message.Data) ~= "string" or #message.Data > 1024 then
        return
    end
    local decoded, command = pcall(HttpService.JSONDecode, HttpService, message.Data)
    if not decoded or not valid(command, os.time()) or command.sourcePlaceId ~= game.PlaceId
        or command.sourceJobId ~= jobId then
        return
    end
    local player = Players:GetPlayerByUserId(command.userId)
    if not player then
        return
    end
    -- Keep the receipt schema fixed, even if the publisher supplied extra fields.
    command = {
        version = 1,
        universeId = universeId,
        operationId = command.operationId,
        userId = command.userId,
        sourcePlaceId = command.sourcePlaceId,
        sourceJobId = command.sourceJobId,
        destinationPlaceId = command.destinationPlaceId,
        destinationJobId = command.destinationJobId,
        issuedUtc = command.issuedUtc,
        expiresUtc = command.expiresUtc,
    }

    bounded(command.userId, command.destinationPlaceId and "move" or "status check", function()
        local claimId = string.lower(HttpService:GenerateGUID(false))
        local stage = command.destinationPlaceId and "received" or "atSource"
        local accepted = store:UpdateAsync(key(command.userId), function(previous)
            local now = os.time()
            if not fresh(command, now) or player.Parent ~= Players then
                return nil
            end
            if type(previous) == "table" and type(previous.command) == "table" then
                local prior = previous.command
                -- One live move per player, for its whole 45 s, so a move is never repeated. A status check
                -- is finished once written, so a newer status or move may replace it. An out-of-order
                -- request never replaces anything.
                if prior.operationId == command.operationId
                    or (type(prior.expiresUtc) == "number" and prior.expiresUtc > now and previous.stage ~= "atSource")
                    or (type(prior.issuedUtc) == "number" and prior.issuedUtc >= command.issuedUtc) then
                    return nil
                end
            end
            return receipt(command, stage, claimId, now)
        end)
        if not command.destinationPlaceId or not claimed(accepted, command.operationId, claimId)
            or player.Parent ~= Players or not fresh(command, os.time()) then
            return
        end

        local options = Instance.new("TeleportOptions")
        options.ServerInstanceId = command.destinationJobId
        options:SetTeleportData({ zenithOperationId = command.operationId, zenithClaimId = claimId })
        TeleportService:TeleportAsync(command.destinationPlaceId, { player }, options)
        updateStage(command.userId, command.operationId, claimId, "teleportInitiated", function(previous)
            return previous.stage == "received"
        end)
    end)
end

local function onArrival(player)
    if not allowedUsers[player.UserId] then
        return
    end
    local join = player:GetJoinData()
    local data = join.TeleportData
    if join.SourceGameId ~= universeId or not allowedPlaces[join.SourcePlaceId]
        or type(data) ~= "table" or not isGuid(data.zenithOperationId) or not isGuid(data.zenithClaimId) then
        return
    end
    bounded(player.UserId, "arrival receipt", function()
        updateStage(player.UserId, data.zenithOperationId, data.zenithClaimId, "arrived", function(previous)
            local command = previous.command
            return player.Parent == Players and (previous.stage == "received" or previous.stage == "teleportInitiated")
                and command.sourcePlaceId == join.SourcePlaceId and command.destinationPlaceId == game.PlaceId
                and command.destinationJobId == jobId
        end)
    end, true)
end

local function onTeleportFailed(player, _, _, placeId, options)
    if not allowedUsers[player.UserId] or not options then
        return
    end
    local data = options:GetTeleportData()
    if type(data) ~= "table" or not isGuid(data.zenithOperationId) or not isGuid(data.zenithClaimId) then
        return
    end
    bounded(player.UserId, "teleport failure receipt", function()
        updateStage(player.UserId, data.zenithOperationId, data.zenithClaimId, "rejected", function(previous)
            local command = previous.command
            return player.Parent == Players and (previous.stage == "received" or previous.stage == "teleportInitiated")
                and command.sourceJobId == jobId and command.sourcePlaceId == game.PlaceId
                and command.destinationPlaceId == placeId and command.destinationJobId == options.ServerInstanceId
        end)
    end, true)
end

Players.PlayerAdded:Connect(onArrival)
TeleportService.TeleportInitFailed:Connect(onTeleportFailed)

-- Roblox can throttle or briefly fail SubscribeAsync. Without a subscription this server would ignore every
-- command for its whole lifetime, so keep retrying with a capped backoff. This is the script's last statement.
local retryDelay = 1
while true do
    local subscribed, failure = pcall(MessagingService.SubscribeAsync, MessagingService, "ZenithSessionsV1", onMessage)
    if subscribed then
        break
    end
    warn(string.format("Zenith Sessions bridge could not subscribe to commands (%s). Retrying in %d s.",
        tostring(failure), retryDelay))
    task.wait(retryDelay)
    retryDelay = math.min(retryDelay * 2, 60)
end
