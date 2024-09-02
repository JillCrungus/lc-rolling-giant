using GameNetcodeStuff;
using RollingGiant.Settings;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace RollingGiant;

public class RollingGiantAI : EnemyAI {
   private const float ROAMING_AUDIO_PERCENT = 0.4f;

#pragma warning disable 649
   [SerializeField] private AISearchRoutine _searchForPlayers;
   [SerializeField] private Collider _mainCollider;
   [SerializeField] private AudioClip[] _stopNoises;
#pragma warning restore 649

   private static RollingGiantAiType _aiType => NetworkHandler.AiType;
   private static SharedAiSettings _sharedAiSettings => CustomConfig.SharedAiSettings;
   
   private AudioSource _rollingSFX;
   private NetworkVariable<float> _velocity = new();
   private NetworkVariable<float> _waitTimer = new();
   private NetworkVariable<float> _moveTimer = new();
   private NetworkVariable<float> _lookTimer = new();
   private NetworkVariable<float> _aggroTimer = new();

   private float _timeSinceHittingPlayer;
   private bool _wantsToChaseThisClient;
   private bool _hasEnteredChaseState;
   private bool _wasStopped;
   private bool _wasFeared;
   private bool _isAggro;
   private float _lastSpeed;
   private bool _tooBig;

   private static void LogInfo(object message) {
#if DEBUG
	  Plugin.Log.LogInfo(message);
#endif
   }

   private static float NextDouble() {
	  if (!RoundManager.Instance || RoundManager.Instance.LevelRandom == null) {
		 return Random.value;
	  }
	  
	  return (float)RoundManager.Instance.LevelRandom.NextDouble();
   }

   public override void Start() {
	  base.Start();
	  
	  Init(transform.localScale.x);
	  
	  if (IsHost || IsOwner) {
		 AssignInitData_LocalClient();
	  }
	  
	  LogInfo($"Rolling giant spawned with ai type: {NetworkHandler.AiType}, owner? {IsOwner}");
   }

   private void Init(float scale) {
	  agent = gameObject.GetComponentInChildren<NavMeshAgent>();
	  _rollingSFX = transform.Find("RollingSFX").GetComponent<AudioSource>();
	  
	  var mixer = SoundManager.Instance.diageticMixer.outputAudioMixerGroup;
	  _rollingSFX.outputAudioMixerGroup = mixer;
	  creatureVoice.outputAudioMixerGroup = mixer;
	  creatureSFX.outputAudioMixerGroup = mixer;
	  
	  _rollingSFX.loop = true;
	  _rollingSFX.clip = Plugin.WalkSound;
	  
	  var time = NextDouble() * Plugin.WalkSound.length;
	  _rollingSFX.time = time;
	  _rollingSFX.pitch = Mathf.Lerp(1.1f, 0.8f, Mathf.InverseLerp(0.9f, 1.2f, scale));
	  _rollingSFX.volume = 0;
	  _rollingSFX.Play();
	  
	  isOutside = enemyType.isOutsideEnemy || enemyType.isDaytimeEnemy;
	  SetEnemyOutside(isOutside);
   }

   public void ResetValues() {
	  if (IsHost || IsServer) {
		 _waitTimer.Value = 0;
		 _moveTimer.Value = 0;
		 _lookTimer.Value = 0;
		 _aggroTimer.Value = 0;
		 SwitchToBehaviourState(0);
		 EndChasingPlayer_ClientRpc();
		 ResetValues_ClientRpc();
	  }
   }

   [ClientRpc]
   private void ResetValues_ClientRpc() {
	  _isAggro = false;
	  _wasStopped = false;
	  _wasFeared = false;
   }

   public override void DaytimeEnemyLeave() {
	  base.DaytimeEnemyLeave();
	  
	  foreach (Renderer renderer in transform.GetComponentsInChildren<Renderer>()) {
		 if (renderer.name == "object_3") continue;
		 renderer.sharedMaterial = Plugin.BlackAndWhiteMaterial;
	  }

	  _mainCollider.isTrigger = true;
   }

   public override void DoAIInterval() {
	  if (daytimeEnemyLeaving) {
		 _mainCollider.isTrigger = true;
		 return;
	  }
	  
	  base.DoAIInterval();
	  
	  if (StartOfRound.Instance.livingPlayers == 0 || isEnemyDead) return;
	  
	  // LogInfo($"{agent.speed}, {_velocity.Value}");
   
	  switch (currentBehaviourStateIndex) {
		 // searching
		 case 0:
			if (!IsServer) {
			   ChangeOwnershipOfEnemy(StartOfRound.Instance.allPlayerScripts[0].actualClientId);
			   break;
			}
   
			if (!_searchForPlayers.inProgress) {
			   StartSearch(transform.position, _searchForPlayers);
			   LogInfo($"[DoAIInterval::{NetworkHandler.AiType}] StartSearch({transform.position}, _searchForPlayers)");
			   return;
			}

			foreach (PlayerControllerB player in StartOfRound.Instance.allPlayerScripts) {
			   if (player.isPlayerDead) continue;
			   if (!player.IsSpawned) continue;
			   if (isOutside == player.isInsideFactory) continue; //We're outside and the player isn't
			   if (!isOutside && !PlayerIsTargetable(player)) continue; //We're inside but the player isn't targetable

			   float distance = Vector3.Distance(transform.position, player.transform.position);
			   bool inRange = distance < (isOutside ? 90 : 30);
			   if (!Physics.Linecast(transform.position + Vector3.up * 0.5f, player.gameplayCamera.transform.position, StartOfRound.Instance.collidersAndRoomMaskAndDefault) && inRange) {
				  SwitchToBehaviourState(1);
				  LogInfo($"[DoAIInterval::{NetworkHandler.AiType}] SwitchToBehaviourState(1), found {player?.playerUsername} at distance {distance}m");
				  return;
			   }
			}
			
			if (isOutside) {
			   var closest = GetClosestPlayer();
			   if (closest && !Physics.Linecast(transform.position + Vector3.up * 0.5f, closest.gameplayCamera.transform.position, StartOfRound.Instance.collidersAndRoomMaskAndDefault)) {
				  float distance = Vector3.Distance(transform.position, closest.transform.position);
				  bool inRange = distance < (isOutside ? 90 : 30);
				  if (inRange) {
					 targetPlayer = closest;
					 SwitchToBehaviourState(1);
					 LogInfo($"[DoAIInterval::{NetworkHandler.AiType}] ClosestPlayer! SwitchToBehaviourState(1), found {targetPlayer?.playerUsername}");
				  }
			   }
			}
			break;
		 // chasing
		 case 1:
			// not in range of any player, so go back to wandering
			if (!TargetClosestPlayer()) {
			   movingTowardsTargetPlayer = false;
			   if (_searchForPlayers.inProgress) {
				  break;
			   }
			   SwitchToBehaviourState(0);
			   LogInfo($"[DoAIInterval::{NetworkHandler.AiType}] lost player; StartSearch({transform.position}, _searchForPlayers)");
			   break;
			}
   
			if (!_searchForPlayers.inProgress) {
			   break;
			}
   
			// stop the current search as we found a player!
			StopSearch(_searchForPlayers);
			movingTowardsTargetPlayer = true;
			LogInfo($"[DoAIInterval::{NetworkHandler.AiType}] StopSearch(_searchForPlayers), found {targetPlayer?.playerUsername}");
			break;
	  }
   }

	public override void Update()
	{
		if (daytimeEnemyLeaving)
		{
			_mainCollider.isTrigger = true;
			_rollingSFX.volume = 0;
			return;
		}

		if (IsHost || IsServer)
		{
			_velocity.Value = agent.velocity.magnitude;
		}

		base.Update();

		if (isEnemyDead) return;

		_lastSpeed = agent.velocity.magnitude;
		CalculateAgentSpeed();

		_timeSinceHittingPlayer += Time.deltaTime;
		_mainCollider.isTrigger = _velocity.Value > 0.01f;

		var speed = _velocity.Value;

		if (speed > 0.1f)
		{
			_rollingSFX.volume = Mathf.Lerp(0, Mathf.Clamp01(ROAMING_AUDIO_PERCENT * speed + 0.05f), speed / _sharedAiSettings.moveSpeed);
		}
		else
		{
			_rollingSFX.volume = Mathf.Lerp(_rollingSFX.volume, 0, Time.deltaTime);
		}

		var gameNetworkManager = GameNetworkManager.Instance;
		var localPlayer = gameNetworkManager.localPlayerController;
		if (_wasStopped && !_wasFeared)
		{
			if (localPlayer.HasLineOfSightToPosition(eye.position, 70, 25))
			{
				_wasFeared = true;

				var distance = Vector3.Distance(transform.position, localPlayer.transform.position);
				if (distance < 4)
				{
					gameNetworkManager.localPlayerController.JumpToFearLevel(0.9f);
				}
				else if (distance < 9)
				{
					gameNetworkManager.localPlayerController.JumpToFearLevel(0.4f);
				}

				if (_lastSpeed > 1)
				{
					RoundManager.PlayRandomClip(creatureVoice, _stopNoises, false);
				}
			}
		}

		switch (currentBehaviourStateIndex)
		{
			// searching
			case 0:
			{
				if (_hasEnteredChaseState)
				{
					_hasEnteredChaseState = false;
					_wantsToChaseThisClient = false;
					_wasStopped = false;
					_wasFeared = false;
					agent.speed = 0;
					if (_aiType is not RollingGiantAiType.OnceSeenAgroAfterTimer)
					{
						_isAggro = false;
						if (IsOwner)
						{
							_aggroTimer.Value = 0;
						}
					}
					if (IsOwner)
					{
						_waitTimer.Value = 0;
						_moveTimer.Value = 0;
						_lookTimer.Value = 0;
					}
				}

				if (IsOwner)
				{
					if (TargetClosestPlayer(requireLineOfSight: true))
					{
						if (_wantsToChaseThisClient)
						{
							break;
						}

						_wantsToChaseThisClient = true;
						BeginChasingPlayer_ServerRpc((int)targetPlayer.playerClientId);
						LogInfo($"[Update::{NetworkHandler.AiType}] began chasing local player {targetPlayer?.playerUsername}");
					}
				}
			}
			break;
			// chasing
			case 1:
			{
				if (!_hasEnteredChaseState)
				{
					_hasEnteredChaseState = true;
					_wantsToChaseThisClient = false;
					_wasStopped = false;
					_wasFeared = false;
					if (_aiType is not RollingGiantAiType.OnceSeenAgroAfterTimer)
					{
						_isAggro = false;
						if (IsOwner)
						{
							_aggroTimer.Value = 0;
						}
					}
					if (IsOwner)
					{
						_waitTimer.Value = 0;
						_moveTimer.Value = 0;
						_lookTimer.Value = 0;
					}
				}

				if (stunNormalizedTimer > 0)
				{
					break;
				}

				var lastPlayer = targetPlayer;
				if (IsOwner)
				{
					// nobody is in range so go back to searching
					if (!isOutside && !TargetClosestPlayer())
					{
						SwitchToBehaviourState(0);
						EndChasingPlayer_ServerRpc();
						LogInfo($"[Update::{NetworkHandler.AiType}] not in range; SwitchToBehaviourState(0)");
						break;
					}

					if (isOutside && !TargetClosestPlayer())
					{
						var player = GetClosestPlayer();
						if (!player)
						{
							SwitchToBehaviourState(0);
							EndChasingPlayer_ServerRpc();
							LogInfo($"[Update::{NetworkHandler.AiType}] not in range; SwitchToBehaviourState(0)");
							break;
						}
						targetPlayer = player;
					}

					if (_wasStopped && _sharedAiSettings.rotateToLookAtPlayer)
					{
						if (_lookTimer.Value >= _sharedAiSettings.delayBeforeLookingAtPlayer)
						{
							// rotate visuals to look at player
							var lookAt = targetPlayer.transform.position;
							var position = transform.position;
							var dir = lookAt - position;
							dir.y = 0;
							dir.Normalize();

							var quaternion = Quaternion.LookRotation(dir);
							transform.rotation = Quaternion.Lerp(transform.rotation, quaternion, Time.deltaTime / _sharedAiSettings.lookAtPlayerDuration);
						}
					}
				}

				var aiType = NetworkHandler.AiType;
				switch (aiType)
				{
					case RollingGiantAiType.Coilhead:
						if (AmIBeingLookedAt(out _))
						{
							_wasStopped = true;
							return;
						}
						break;
					case RollingGiantAiType.InverseCoilhead:
						if (!AmIBeingLookedAt(out _) && _isAggro && CheckLineOfSightToAnyPlayer())
						{
							_wasStopped = true;
							return;
						}
						break;
					case RollingGiantAiType.RandomlyMoveWhileLooking:
						if (AmIBeingLookedAt(out _) && _moveTimer.Value <= 0)
						{
							_wasStopped = true;
							return;
						}
						break;
					case RollingGiantAiType.LookingTooLongKeepsAgro:
						if (AmIBeingLookedAt(out _) && _aggroTimer.Value < 1f)
						{
							_wasStopped = true;
							return;
						}
						break;
					case RollingGiantAiType.FollowOnceAgro:
						// ?
						break;
					case RollingGiantAiType.OnceSeenAgroAfterTimer:
						if (_isAggro && _aggroTimer.Value > 0)
						{
							_wasStopped = true;
							return;
						}
						break;
					default:
						Plugin.Log.LogWarning($"Unknown ai type: {aiType}");
						break;
				}

				_wasStopped = false;
				_wasFeared = false;

				if (IsOwner)
				{
					if (lastPlayer != targetPlayer)
					{
						SetMovingTowardsTargetPlayer(targetPlayer);
						LogInfo($"[Update::{NetworkHandler.AiType}] SetMovingTowardsTargetPlayer, player {targetPlayer?.playerUsername}");
					}
				}
			}
			break;
		}
	}
   private static float SmoothLerp(float a, float b, float t) {
	  return a + (t * t) * (b - a);
   }

   public override void OnCollideWithPlayer(Collider other) {
	  if (daytimeEnemyLeaving) {
		 return;
	  }
	  
	  base.OnCollideWithPlayer(other);
	  if (_timeSinceHittingPlayer < 0.6f) {
		 return;
	  }

	  var player = MeetsStandardPlayerCollisionConditions(other);
	  if (!player) return;

	  if (_tooBig && player.isInHangarShipRoom) {
		 return;
	  }

	  _timeSinceHittingPlayer = 0.2f;
	  var index = StartOfRound.Instance.playerRagdolls.IndexOf(Plugin.PlayerRagdoll);
	  player.DamagePlayer(90, causeOfDeath: CauseOfDeath.Strangulation, deathAnimation: index);
	  agent.speed = 0;

	  GameNetworkManager.Instance.localPlayerController.JumpToFearLevel(1f);
   }

   private void CalculateAgentSpeed() {
	  if (stunNormalizedTimer >= 0) {
		 agent.speed = 0;
		 agent.acceleration = 200;
		 return;
	  }

	  // searching
	  if (currentBehaviourStateIndex == 0) {
		 MoveAccelerate();
	  }
	  // chasing
	  else if (currentBehaviourStateIndex == 1) {
		 // if not on the ground, accelerate to reach it
		 if (IsOwner && !IsAgentOnNavMesh(agent.gameObject)) {
			MoveAccelerate();
			LogInfo($"[CalculateAgentSpeed::{NetworkHandler.AiType}] not on navmesh");
			return;
		 }

		 bool isLookedAt = AmIBeingLookedAt(out _);
		 if (IsOwner) {
			if (isLookedAt) {
			   _lookTimer.Value += Time.deltaTime;
			} else {
			   _lookTimer.Value = 0;
			}
		 }

		 var aiType = NetworkHandler.AiType;
		 switch (aiType) {
			case RollingGiantAiType.Coilhead:
			   if (isLookedAt) {
				  MoveDecelerate();
				  return;
			   }

			   MoveAccelerate();
			   break;
			case RollingGiantAiType.InverseCoilhead:
			   //TODO: What's actually going on here?
			   if (!isLookedAt && _isAggro) {
				  if (CheckLineOfSightToAnyPlayer()) {
					 MoveDecelerate();
					 return;
				  }
			   }

			   MoveAccelerate();
			   _isAggro = true;
			   break;
			case RollingGiantAiType.RandomlyMoveWhileLooking:
			   if (isLookedAt) {
				  if (_waitTimer.Value <= 0 && _moveTimer.Value <= 0) {
					 GenerateWaitTime();
				  }

				  if (_waitTimer.Value > 0 && _moveTimer.Value <= 0) {
					 MoveDecelerate();

					 if (IsOwner) {
						LogInfo($"_waitTimer: {_waitTimer.Value}");
						_waitTimer.Value -= Time.deltaTime;
						if (_waitTimer.Value <= 0) {
						   GenerateMoveTime();
						}
					 }
					 return;
				  }
			   }

			   MoveAccelerate();

			   if (_moveTimer.Value > 0) {
				  if (IsOwner) {
					 LogInfo($"_moveTimer: {_moveTimer.Value}");
					 _moveTimer.Value -= Time.deltaTime;
					 if (_moveTimer.Value <= 0) {
						GenerateWaitTime();
					 }
				  }
			   }
			   break;
			case RollingGiantAiType.LookingTooLongKeepsAgro:
			   if (!_isAggro) {
				  if (isLookedAt) {
					 if (IsOwner) {
						_aggroTimer.Value += Time.deltaTime / _sharedAiSettings.lookTimeBeforeAgro;
					 }
					 LogInfo($"[Update::{NetworkHandler.AiType}] _agroTimer: {_aggroTimer.Value}");
					 if (_aggroTimer.Value >= 1f) {
						_isAggro = true;
						LogInfo($"[Update::{NetworkHandler.AiType}] got agro");
					 }

					 MoveDecelerate();
				  } else {
					 if (IsOwner) {
						_aggroTimer.Value = Mathf.Lerp(_aggroTimer.Value, 0, Time.deltaTime / (_sharedAiSettings.lookTimeBeforeAgro * 1.5f));
						LogInfo($"[Update::{NetworkHandler.AiType}] _agroTimer: {_aggroTimer.Value}");
					 }

					 MoveAccelerate();
				  }
				  return;
			   }

			   MoveAccelerate();
			   break;
			case RollingGiantAiType.FollowOnceAgro:
			   if (!_isAggro) {
				  if (isLookedAt) {
					 _isAggro = true;
					 MoveDecelerate();
					 LogInfo($"[Update::{NetworkHandler.AiType}] got aggro");
					 return;
				  }
			   }

			   MoveAccelerate();
			   break;
			case RollingGiantAiType.OnceSeenAgroAfterTimer:
			   if (!_isAggro) {
				  if (isLookedAt) {
					 _isAggro = true;
					 LogInfo($"[Update::{NetworkHandler.AiType}] got aggro");
					 if (IsOwner) {
						_aggroTimer.Value = Mathf.Lerp(_sharedAiSettings.waitTimeMin, _sharedAiSettings.waitTimeMax, NextDouble());
					 }
					 MoveDecelerate();
					 return;
				  }
			   } else if (_aggroTimer.Value > 0) {
				  LogInfo($"[Update::{NetworkHandler.AiType}] _aggroTimer: {_aggroTimer.Value}");
				  if (IsOwner) {
					 _aggroTimer.Value -= Time.deltaTime;
				  }

				  if (_aggroTimer.Value <= 0) {
					 LogInfo($"[Update::{NetworkHandler.AiType}] chasing time");
				  }
				  MoveDecelerate();
				  return;
			   }

			   MoveAccelerate();
			   break;
			default:
			   Plugin.Log.LogWarning($"Unknown ai type: {aiType}");
			   break;
		 }
	  }
   }

   private void MoveAccelerate() {
	  agent.speed = _sharedAiSettings.moveAcceleration == 0
		 ? _sharedAiSettings.moveSpeed
		 : Mathf.Lerp(agent.speed, _sharedAiSettings.moveSpeed, Time.deltaTime / _sharedAiSettings.moveAcceleration);
	  agent.acceleration = Mathf.Lerp(agent.acceleration, 200, Time.deltaTime);
   }

   private void MoveDecelerate() {
	  agent.speed = _sharedAiSettings.moveDeceleration == 0 ? 0 : Mathf.Lerp(agent.speed, 0, Time.deltaTime / _sharedAiSettings.moveDeceleration);
	  agent.acceleration = 200;
   }

	

   private bool AmIBeingLookedAt(out PlayerControllerB closestPlayer) {
	  PlayerControllerB[] players = StartOfRound.Instance.allPlayerScripts;
	  float closestDistance = float.MaxValue;
	  closestPlayer = null;

	  foreach (PlayerControllerB player in players) {
		 if (!isOutside && !PlayerIsTargetable(player)) continue;
		 if (player.isPlayerDead) continue;
		 if (!player.IsSpawned) continue;
		 if (!player.HasLineOfSightToPosition(transform.position + Vector3.up * 1.6f, 68f)) continue;

		 float distance = Vector3.Distance(transform.position, player.transform.position);
		 if (distance < closestDistance)
		 {
		 	closestDistance = distance;
			closestPlayer = player;
		 }
		}

	  return closestPlayer;
   }

   private bool CheckLineOfSightTo(PlayerControllerB player) {
		return player && CheckLineOfSightForPosition(player.gameplayCamera.transform.position, 68f);
   }
   
   private bool CheckLineOfSightToAnyPlayer() {
	  var players = StartOfRound.Instance.allPlayerScripts;
	  foreach (var player in players) {
		 if (!player.IsSpawned) continue;
		 if (player.isPlayerDead) continue;
		 
		 if (CheckLineOfSightTo(player)) {
			return true;
		 }
	  }

	  return false;
   }

   private bool IsAgentOnNavMesh(GameObject agentObject) {
	  var agentPosition = agentObject.transform.position;

	  // Check for nearest point on navmesh to agent, within onMeshThreshold
	  if (NavMesh.SamplePosition(agentPosition, out NavMeshHit hit, 3, NavMesh.AllAreas)) {
		 // Check if the positions are vertically aligned
		 if (Mathf.Approximately(agentPosition.x, hit.position.x)
			 && Mathf.Approximately(agentPosition.z, hit.position.z)) {
			// Lastly, check if object is below navmesh
			return agentPosition.y >= hit.position.y;
		 }
	  }

	  return false;
   }

   [ServerRpc(RequireOwnership = false)]
   private void BeginChasingPlayer_ServerRpc(int playerId) {
	  BeginChasingPlayer_ClientRpc(playerId);
   }

   [ClientRpc]
   private void BeginChasingPlayer_ClientRpc(int playerId) {
	  SwitchToBehaviourStateOnLocalClient(1);
	  var player = StartOfRound.Instance.allPlayerScripts[playerId];
	  SetMovingTowardsTargetPlayer(player);
   }
   
   [ServerRpc(RequireOwnership = false)]
   private void EndChasingPlayer_ServerRpc() {
	  EndChasingPlayer_ClientRpc();
   }
   
   [ClientRpc]
   private void EndChasingPlayer_ClientRpc() {
	  movingTowardsTargetPlayer = false;
	  SwitchToBehaviourStateOnLocalClient(0);
   }
   
   private void GenerateWaitTime() {
	  if (!IsOwner) return;
	  var waitTime = Mathf.Lerp(_sharedAiSettings.waitTimeMin, _sharedAiSettings.waitTimeMax, NextDouble());
	  _waitTimer.Value = waitTime;
   }
   
   private void GenerateAgroTime() {
	  if (!IsOwner) return;
	  _aggroTimer.Value = _sharedAiSettings.lookTimeBeforeAgro;
   }
   
   private void GenerateMoveTime() {
	  if (!IsOwner) return;
	  var moveTime = Mathf.Lerp(_sharedAiSettings.randomMoveTimeMin, _sharedAiSettings.randomMoveTimeMax, NextDouble());
	  _moveTimer.Value = moveTime;
   }

   [ServerRpc(RequireOwnership = false)]
   private void AssignInitData_ServerRpc(float scale) {
	  AssignAgentData(scale);
	  agent.transform.localScale = Vector3.one * scale;
	  AssignInitData_ClientRpc(scale);
   }
   
   private void AssignInitData_LocalClient() {
	  var config = CustomConfig.Instance;
	  var sizeRange = isOutside ? new Vector2(config.GiantScaleOutsideMin, config.GiantScaleOutsideMax) : new Vector2(config.GiantScaleInsideMin, config.GiantScaleInsideMax);
	  var scale = Mathf.Lerp(sizeRange.x, sizeRange.y, NextDouble());
	  AssignAgentData(scale);
	  agent.transform.localScale = Vector3.one * scale;
	  AssignInitData_ServerRpc(scale);
   }

   [ClientRpc]
   private void AssignInitData_ClientRpc(float scale) {
	  // agent.height = 5f;
	  updatePositionThreshold = float.MaxValue;
	  AssignAgentData(scale);
	  agent.transform.localScale = Vector3.one * scale;
	  
   }

   private void AssignAgentData(float scale) {
	  Init(scale);
	  if (scale >= 1.2f) {
		 var areas = agent.areaMask;
		 areas &= ~(1 << NavMesh.GetAreaFromName("SmallSpace"));
		 areas &= ~(1 << NavMesh.GetAreaFromName("MediumSpace"));
		 areas &= ~(1 << NavMesh.GetAreaFromName("Climb"));
		 areas &= ~(1 << NavMesh.GetAreaFromName("PlayerShip"));
		 agent.areaMask = areas;
		 _tooBig = true;
	  }
   }
}