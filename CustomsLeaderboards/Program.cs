using CustomsLeaderboards;
using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CustomLeaderboards
{
	class Program
	{
		private static HttpClient client = new HttpClient();
		private static MongoClient dbClient = new MongoClient("mongodb://localhost");
		private static IMongoDatabase db = dbClient.GetDatabase("CLD");
		private static readonly string apiRoot = "https://new.scoresaber.com";
		private static readonly string apiPlayers = apiRoot + "/api/players/<page>";
		private static readonly string apiScores = apiRoot + "/api/player/<ssid>/scores/recent/<page>";
		private static readonly string apiScoresTop = apiRoot + "/api/player/<ssid>/scores/top/<page>";

		private static bool willExit = false;

		private static List<string> LeaderboardList = new List<string>()
		{
			"CountryWeightedPP",
			"CountryRawPP",
			"CountryWeightedRankAverage",
			"CountryRankAverage",
			"CountryBestRank",
			"CountryAverageScorePercentage",
			"CountryWeightedAverageScorePercentage"
		};

		private static int nbOfPlayersToLookFor = 3000;

		static void Main(string[] args)
		{
			Console.CancelKeyPress += Console_CancelKeyPress;

			// Get map List
			List<Map> maps = db.GetCollection<Map>("maps").Find(new BsonDocument()).ToList();

			while (!willExit)
			{
				// Variable initialization
				Stopwatch sp = Stopwatch.StartNew();

				// Get player list
				Console.WriteLine("Refreshing player list...");
				List<Profile> profiles = GetPlayerList();

				// Actualise or get new players and maps
				Console.WriteLine("Updating player scores...");
				List<Score> scores = GetAndRefreshPlayersScores(profiles, ref maps);

				// Compute ranks
				Console.WriteLine("Generating ranks...");
				scores = SetRanks(scores, maps, profiles);

				// Compute profile datas
				Console.WriteLine("Generating profile data...");
				List<ProfileData> profileDatas = ComputeProfileData(scores, maps, profiles);

				// Compute country datas
				Console.WriteLine("Generating country data...");
				List<CountryData> countryDatas = ComputeCountryData(profileDatas, profiles);

				// Generate Leaderboards
				Console.WriteLine("Generating leaderboards...");
				List<Leaderboards> leaderboards = GenerateLeaderboards(profileDatas, countryDatas);

				// Check if you need a snapshot and make it
				Console.WriteLine("Generating snapshots...");
				GenerateSnapshot(leaderboards);

				sp.Stop();
				Console.WriteLine("All finished, took " + sp.Elapsed.TotalSeconds + "s\n\n");
			}

			Console.WriteLine("Press enter to quit...");
			Console.ReadLine();
		}

		private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
		{
			if (!willExit)
			{
				Console.WriteLine("\nKill signal has been received. Exiting at the end of this loop...");
				e.Cancel = true;
				willExit = true;
			} else
			{
				Console.WriteLine("\nKill signal received a second time. Force exiting the app...");
			}
		}

		// Get every player in the top 3000, and add them to the DB if not there yet
		private static List<Profile> GetPlayerList()
		{
			Stopwatch sp = Stopwatch.StartNew();
			IMongoCollection<Profile> dbCollection = db.GetCollection<Profile>("profiles");
			List<Profile> profiles = dbCollection.Find(new BsonDocument()).ToList();
			PlayerResponseWrap response;
			Profile tmp;

			// For nbOfPlayersToLookFor pages
			for (int i = 1; i < (nbOfPlayersToLookFor / 50) + 1; i++)
			{
				// Get the page from ScoreSaber API and iterate over each entry
				response = GetAsyncFromScoresaber<PlayerResponseWrap>(apiPlayers, i);

				for (int j = 0; j < response.players.Count; j++)
				{
					// Check if the entry already exists in DB
					tmp = profiles.Find(p => p.SSID == response.players[j].playerId);

					// If it exists and something changed, update it
					if (tmp != null && (tmp.nickname != response.players[j].playerName || tmp.avatarPath != response.players[j].avatar))
					{
						// Update in DB
						var filter = Builders<Profile>.Filter.Eq("SSID", tmp.SSID);
						var update = Builders<Profile>.Update.Set("nickname", response.players[j].playerName).Set("avatarPath", response.players[j].avatar);
						dbCollection.UpdateOne(filter, update);
						// And in collection
						profiles.Remove(tmp);
						profiles.Add(new Profile() { SSID = tmp.SSID, nickname = response.players[j].playerName, avatarPath = response.players[j].avatar, country = tmp.country, last = tmp.last });
					} 

					// If it doesn't exist, add it
					else if(tmp == null)
					{
						tmp = new Profile() { SSID = response.players[j].playerId, nickname = response.players[j].playerName, avatarPath = response.players[j].avatar, country = response.players[j].country };
						dbCollection.InsertOne(tmp);
						profiles.Add(tmp);
					}
				}

				Thread.Sleep((80 / 60) * 1000);
			}

			sp.Stop();
			Console.WriteLine("Player list refreshed, took " + sp.Elapsed.TotalSeconds + "s");
			// Return the updated list of profiles
			return profiles;
		}

		// Actualise player scores and add new maps
		private static List<Score> GetAndRefreshPlayersScores(List<Profile> profiles, ref List<Map> maps)
		{
			Stopwatch sp = Stopwatch.StartNew(), sp2;
			int page = 1, count = 0, tmpScore, totalCount = 0;
			ScoreResponseWrap response;
			IMongoCollection<Score> dbCollectionScores = db.GetCollection<Score>("scores");
			IMongoCollection<Profile> dbCollectionProfiles = db.GetCollection<Profile>("profiles");
			IMongoCollection<Map> dbCollectionMaps = db.GetCollection<Map>("maps");
			bool stop = false, timeChanged = false, isNew = false;

			for(int i = 0; i < profiles.Count; i++)
			{
				sp2 = Stopwatch.StartNew();
				stop = false;
				timeChanged = false;
				page = 1;
				count = 0;
				isNew = false;

				// Is a new profile ?
				if (profiles[i].last == DateTime.MinValue)
					isNew = true;

				while(!stop)
				{
					// If new, get top scores (because ranked are shown first, less pages to check)
					if(!isNew)
						response = GetAsyncFromScoresaber<ScoreResponseWrap>(apiScores, page, profiles[i].SSID);
					else
						response = GetAsyncFromScoresaber<ScoreResponseWrap>(apiScoresTop, page, profiles[i].SSID);

					// If no scores are found (blank page), then break and go next profile. (Also check if unrank in case of a new profile)
					if (response == null || response.scores == null || response.scores.Count == 0 || (isNew && response.scores[0].pp == 0))
					{
						stop = true;
						break;
					}

					// Change the time of "last" if needed
					if(!timeChanged && profiles[i].last != response.scores[0].timeSet)
					{
						var filter = Builders<Profile>.Filter.Where(p => p.SSID == profiles[i].SSID);
						var update = Builders<Profile>.Update.Set("last", (!isNew) ? response.scores[0].timeSet : DateTime.Now);

						// Do not change it in the profile list, we still need it. It will be automaticly changed next loop (as we will get it from the DB we just updated)
						dbCollectionProfiles.UpdateOne(filter, update);
						timeChanged = true;
					}

					for(int j = 0; j < response.scores.Count; j++)
					{
						// If timeSet is smaller than "last", we already treated this score last loop, break
						if(response.scores[j].timeSet <= profiles[i].last)
						{
							stop = true;
							break;
						}

						if (response.scores[j].pp > 0)
						{
							// If the map does not exist, add it to the DB and to the list
							if (!maps.Any(m => m.LDID == response.scores[j].leaderboardId.ToString()))
							{
								Map m = new Map() { LDID = response.scores[j].leaderboardId.ToString(), maxScore = response.scores[j].maxScore };
								dbCollectionMaps.InsertOne(m);
								maps.Add(m);
							}

							// Ignore maps with negative modifiers and get the unmodifiedScore if positive modifiers
							if (!string.IsNullOrWhiteSpace(response.scores[j].mods))
							{
								if (GetTotalMultiplier(response.scores[j].mods) < 1)
									continue;
								else
									tmpScore = response.scores[j].unmodififiedScore;
							} else
							{
								tmpScore = response.scores[j].score;
							}

							// Insert or update score (upsert = true)
							var filter = Builders<Score>.Filter.Where(s => s.SSID == profiles[i].SSID && s.LDID == response.scores[j].leaderboardId.ToString());

							dbCollectionScores.DeleteOne(filter);
							dbCollectionScores.InsertOne(new Score() { SSID = profiles[i].SSID, LDID = response.scores[j].leaderboardId.ToString(), score = tmpScore, pp = response.scores[j].pp, timeSet = response.scores[j].timeSet });
							count++;
							totalCount++;
						}
					}

					page++;
					Thread.Sleep((80/60) * 1000);
				}


				sp2.Stop();
				try
				{
					if (i > 0)
					{
						Console.SetCursorPosition(0, Console.CursorTop);
						Console.Write("                                                                                      ");
						Console.SetCursorPosition(0, Console.CursorTop);
					}
				} catch (Exception) { }
				Console.Write($"Updating n°{i+1} {profiles[i].nickname} took {sp2.Elapsed.TotalSeconds}s for {count} scores");
			}


			sp.Stop();
			Console.WriteLine("\nPlayer scores refreshed, took " + sp.Elapsed.TotalSeconds + "s for a total of " + totalCount + " scores");
			// Return all scores in database
			return dbCollectionScores.Find(new BsonDocument()).ToList();
		}

		// Set ranks in both the score list and the database
		private static List<Score> SetRanks(List<Score> scores, List<Map> maps, List<Profile> profiles)
		{
			Stopwatch sp = Stopwatch.StartNew();
			Dictionary<string, int> countryRanks;
			IMongoCollection<Score> dbCollectionScores = db.GetCollection<Score>("scores");
			List<Score> scoresTMP;
			string country = "";

			// For each map
			for(int i = 0; i < maps.Count; i++)
			{
				try { 
					if (i > 0)
					{
						Console.SetCursorPosition(0, Console.CursorTop);
						Console.Write("                                                            ");
						Console.SetCursorPosition(0, Console.CursorTop);
					}
				} catch (Exception) { }
			Console.Write("Processing map " + (i + 1));
				// Create new country leaderboards
				countryRanks = new Dictionary<string, int>();

				// Get all the scores for this map and order them
				scoresTMP = scores.Where(s => s.LDID == maps[i].LDID).ToList();
				scoresTMP = scoresTMP.OrderByDescending(s => s.score).ThenByDescending(s => s.timeSet).ToList();

				Console.Write(", Cycling through " + scoresTMP.Count + " scores");
				// For each score, set the corresponding rank
				for(int j = 0; j < scoresTMP.Count; j++)
				{
					// Update it in the database, and get it again later
					country = profiles.Find(p => p.SSID == scoresTMP[j].SSID).country;

					if (!countryRanks.ContainsKey(country))
					{
						countryRanks.Add(country, 1);
					}

					if (scoresTMP[j].rank != j + 1 || scoresTMP[j].countryRank != countryRanks[country])
					{
						var filter = Builders<Score>.Filter.Where(s => s.SSID == scoresTMP[j].SSID && s.LDID == scoresTMP[j].LDID);
						//var update = Builders<Score>.Update.Set("rank", j + 1).Set("countryRank", countryRanks[country]);
						scoresTMP[j].rank = j + 1;
						scoresTMP[j].countryRank = countryRanks[country];

						//dbCollectionScores.UpdateOne(filter, update);
						dbCollectionScores.DeleteOne(filter);
						dbCollectionScores.InsertOne(scoresTMP[j]);
					}

					countryRanks[country]++;
				}
			}

			sp.Stop();
			Console.WriteLine("\nRanks generated, took " + sp.Elapsed.TotalSeconds + "s");
			return dbCollectionScores.Find(new BsonDocument()).ToList();
		}

		// Compute and updates profile datas
		private static List<ProfileData> ComputeProfileData(List<Score> scores, List<Map> maps, List<Profile> profiles)
		{
			Stopwatch sp = Stopwatch.StartNew();
			IMongoCollection<ProfileData> dbCollectionProfileData = db.GetCollection<ProfileData>("profileDatas");
			List<ProfileData> profileDatas = new List<ProfileData>();
			List<Score> scoresTMP;
			ProfileData tmp;
			Map map;
			double weightDivider = 0, weight, percent;

			for (int i = 0; i < profiles.Count; i++)
			{
				Stopwatch sp2 = Stopwatch.StartNew();
				weightDivider = 0;
				tmp = new ProfileData();
				tmp.SSID = profiles[i].SSID;
				scoresTMP = scores.Where(s => s.SSID == profiles[i].SSID).ToList();
				scoresTMP = scoresTMP.OrderByDescending(s => s.pp).ToList();

				for(int j = 0; j < scoresTMP.Count; j++)
				{
					map = maps.Find(m => m.LDID == scoresTMP[j].LDID);
					weight = Math.Pow(0.965, j);
					weightDivider += weight;
					percent = (double)scoresTMP[j].score / map.maxScore;

					tmp.weightedPP += (scoresTMP[j].pp * weight);
					tmp.rawPP += scoresTMP[j].pp;
					tmp.averageScorePercentage += percent;
					tmp.weightedAverageScorePercentage += (percent * weight);
					tmp.averageRank += scoresTMP[j].rank;
					tmp.weightedAverageRank += (scoresTMP[j].rank * weight);
					tmp.averageCountryRank += scoresTMP[j].countryRank;
					tmp.weightedAverageCountryRank += (scoresTMP[j].countryRank * weight);
					tmp.nbOfRankedDiffPlayed++;

					if (scoresTMP[j].pp > 325)
						tmp.nbOf325++;
					if (tmp.bestRank == 0 || scoresTMP[j].rank < tmp.bestRank)
						tmp.bestRank = scoresTMP[j].rank;
					if (percent > 0.95)
						tmp.nbOf95++;
					if (tmp.topPP == 0)				// The first map is always the biggest
						tmp.topPP = scoresTMP[j].pp;
					if (scoresTMP[j].countryRank == 1)
						tmp.nbOfCountryFirst++;
					if (scoresTMP[j].rank == 1)
						tmp.nbOfFirst++;
					if (scoresTMP[j].rank <= 10)
						tmp.nbOfTop10++;

					if (percent > 0.99 && tmp.highest99 == 0)
						tmp.highest99 = scoresTMP[j].pp;
					if (percent > 0.98 && tmp.highest98 == 0)
						tmp.highest98 = scoresTMP[j].pp;
					if (percent > 0.97 && tmp.highest97 == 0)
						tmp.highest97 = scoresTMP[j].pp;
					if (percent > 0.96 && tmp.highest96 == 0)
						tmp.highest96 = scoresTMP[j].pp;
				}

				tmp.averageScorePercentage /= tmp.nbOfRankedDiffPlayed;
				tmp.weightedAverageScorePercentage /= weightDivider;
				tmp.averageRank /= tmp.nbOfRankedDiffPlayed;
				tmp.weightedAverageRank /= weightDivider;
				tmp.averageCountryRank /= tmp.nbOfRankedDiffPlayed;
				tmp.weightedAverageCountryRank /= weightDivider;

				var filter = Builders<ProfileData>.Filter.Where(p => p.SSID == profiles[i].SSID);

				dbCollectionProfileData.DeleteOne(filter);
				dbCollectionProfileData.InsertOne(tmp);
				profileDatas.Add(tmp);

				sp2.Stop();
			}

			sp.Stop();
			Console.WriteLine("Profile data generated, took " + sp.Elapsed.TotalSeconds + "s");
			return profileDatas;
		}

		// Compute and updates profile datas
		private static List<CountryData> ComputeCountryData(List<ProfileData> profileDatas, List<Profile> profiles)
		{
			Stopwatch sp = Stopwatch.StartNew();
			List<CountryData> countryDatas = new List<CountryData>();
			IMongoCollection<CountryData> dbCollectionCountryData = db.GetCollection<CountryData>("countryDatas");
			Dictionary<string, List<ProfileData>> countryTops = new Dictionary<string, List<ProfileData>>();
			profileDatas = profileDatas.OrderByDescending(l => l.weightedPP).ToList();
			Profile tmp;
			CountryData cld;

			for(int i = 0; i < profileDatas.Count; i++)
			{
				tmp = profiles.Find(p => p.SSID == profileDatas[i].SSID);
				if (!countryTops.ContainsKey(tmp.country))
				{
					countryTops.Add(tmp.country, new List<ProfileData>());
				}
				else if (countryTops[tmp.country].Count < 50)
				{
					countryTops[tmp.country].Add(profileDatas[i]);
				}
			}

			foreach(KeyValuePair<string, List<ProfileData>> kvp in countryTops)
			{
				if (kvp.Value.Count < 50)
					continue;

				cld = new CountryData() { country = kvp.Key, averageScorePercentage = 0, weightedRankAverage = 0, rankAverage = 0, weightedAverageScorePercentage = 0 };
				for(int i = 0; i < kvp.Value.Count; i++)
				{
					cld.weightedPPaverage += kvp.Value[i].weightedPP;
					cld.rawPPAverage += kvp.Value[i].rawPP;
					cld.averageScorePercentage += kvp.Value[i].averageScorePercentage;
					cld.averageOfbestRanks += kvp.Value[i].bestRank;
					cld.weightedRankAverage += kvp.Value[i].weightedAverageRank;
					cld.rankAverage += kvp.Value[i].averageRank;
					cld.weightedAverageScorePercentage += kvp.Value[i].weightedAverageScorePercentage;
					cld.topPPAverage += kvp.Value[i].topPP;
					cld.sumOfRankedDiffPlayed += kvp.Value[i].nbOfRankedDiffPlayed;
					cld.sumOf95 += kvp.Value[i].nbOf95;
					cld.sumOf325 += kvp.Value[i].nbOf325;
					cld.sumOfTop10 += kvp.Value[i].nbOfTop10;
				}

				cld.weightedPPaverage /= 50;
				cld.rawPPAverage /= 50;
				cld.averageScorePercentage /= 50;
				cld.averageOfbestRanks /= 50;
				cld.weightedRankAverage /= 50;
				cld.rankAverage /= 50;
				cld.weightedAverageScorePercentage /= 50;
				cld.topPPAverage /= 50;

				var filter = Builders<CountryData>.Filter.Where(c => c.country == kvp.Key);

				dbCollectionCountryData.DeleteOne(filter);
				dbCollectionCountryData.InsertOne(cld);
				countryDatas.Add(cld);
			}

			sp.Stop();
			Console.WriteLine("Country data generated, took " + sp.Elapsed.TotalSeconds + "s");
			return countryDatas;
		}

		//public double weightedPPaverage, rawPPAverage, averageScorePercentage, averageOfbestRanks, weightedRankAverage, rankAverage, weightedAverageScorePercentage, topPPAverage;
		//public int sumOfRankedDiffPlayed, sumOf95, sumOf325, sumOfTop10;

		private static List<Leaderboards> GenerateLeaderboards(List<ProfileData> profileDatas, List<CountryData> countryDatas)
		{
			Stopwatch sp = Stopwatch.StartNew();
			IMongoCollection<Leaderboards> dbCollectionLeaderboards = db.GetCollection<Leaderboards>("leaderboards");
			List<Leaderboards> leaderboards = new List<Leaderboards>();
			List<Dictionary<string, double>> tmpLeaderboards = new List<Dictionary<string, double>>();

			for (int i = 0; i < 32; i++)
				tmpLeaderboards.Add(new Dictionary<string, double>());

			for(int i = 0; i < profileDatas.Count; i++)
			{
				tmpLeaderboards[0].Add(profileDatas[i].SSID, profileDatas[i].weightedPP);
				tmpLeaderboards[1].Add(profileDatas[i].SSID, profileDatas[i].rawPP);
				tmpLeaderboards[2].Add(profileDatas[i].SSID, profileDatas[i].weightedAverageScorePercentage);
				tmpLeaderboards[3].Add(profileDatas[i].SSID, profileDatas[i].averageScorePercentage);
				tmpLeaderboards[4].Add(profileDatas[i].SSID, profileDatas[i].weightedAverageRank);
				tmpLeaderboards[5].Add(profileDatas[i].SSID, profileDatas[i].averageRank);
				tmpLeaderboards[6].Add(profileDatas[i].SSID, profileDatas[i].weightedAverageCountryRank);
				tmpLeaderboards[7].Add(profileDatas[i].SSID, profileDatas[i].averageCountryRank);
				tmpLeaderboards[8].Add(profileDatas[i].SSID, profileDatas[i].topPP);
				tmpLeaderboards[9].Add(profileDatas[i].SSID, profileDatas[i].highest96);
				tmpLeaderboards[10].Add(profileDatas[i].SSID, profileDatas[i].highest97);
				tmpLeaderboards[11].Add(profileDatas[i].SSID, profileDatas[i].highest98);
				tmpLeaderboards[12].Add(profileDatas[i].SSID, profileDatas[i].highest99);
				tmpLeaderboards[13].Add(profileDatas[i].SSID, profileDatas[i].nbOfRankedDiffPlayed);
				tmpLeaderboards[14].Add(profileDatas[i].SSID, profileDatas[i].nbOf95);
				tmpLeaderboards[15].Add(profileDatas[i].SSID, profileDatas[i].bestRank);
				tmpLeaderboards[16].Add(profileDatas[i].SSID, profileDatas[i].nbOf325);
				tmpLeaderboards[17].Add(profileDatas[i].SSID, profileDatas[i].nbOfCountryFirst);
				tmpLeaderboards[18].Add(profileDatas[i].SSID, profileDatas[i].nbOfFirst);
				tmpLeaderboards[19].Add(profileDatas[i].SSID, profileDatas[i].nbOfTop10);
			}

			for(int i = 0; i < countryDatas.Count; i++)
			{
				tmpLeaderboards[20].Add(countryDatas[i].country, countryDatas[i].weightedPPaverage);
				tmpLeaderboards[21].Add(countryDatas[i].country, countryDatas[i].rawPPAverage);
				tmpLeaderboards[22].Add(countryDatas[i].country, countryDatas[i].averageScorePercentage);
				tmpLeaderboards[23].Add(countryDatas[i].country, countryDatas[i].averageOfbestRanks);
				tmpLeaderboards[24].Add(countryDatas[i].country, countryDatas[i].weightedRankAverage);
				tmpLeaderboards[25].Add(countryDatas[i].country, countryDatas[i].rankAverage);
				tmpLeaderboards[26].Add(countryDatas[i].country, countryDatas[i].weightedAverageScorePercentage);
				tmpLeaderboards[27].Add(countryDatas[i].country, countryDatas[i].topPPAverage);
				tmpLeaderboards[28].Add(countryDatas[i].country, countryDatas[i].sumOfRankedDiffPlayed);
				tmpLeaderboards[29].Add(countryDatas[i].country, countryDatas[i].sumOf95);
				tmpLeaderboards[30].Add(countryDatas[i].country, countryDatas[i].sumOf325);
				tmpLeaderboards[31].Add(countryDatas[i].country, countryDatas[i].sumOfTop10);
			}

			#region OrderBy
			tmpLeaderboards[0] = tmpLeaderboards[0].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[1] = tmpLeaderboards[1].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[2] = tmpLeaderboards[2].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[3] = tmpLeaderboards[3].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[4] = tmpLeaderboards[4].OrderBy(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[5] = tmpLeaderboards[5].OrderBy(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[6] = tmpLeaderboards[6].OrderBy(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[7] = tmpLeaderboards[7].OrderBy(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[8] = tmpLeaderboards[8].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[9] = tmpLeaderboards[9].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[10] = tmpLeaderboards[10].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[11] = tmpLeaderboards[11].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[12] = tmpLeaderboards[12].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[13] = tmpLeaderboards[13].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[14] = tmpLeaderboards[14].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[15] = tmpLeaderboards[15].OrderBy(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[16] = tmpLeaderboards[16].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[17] = tmpLeaderboards[17].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[18] = tmpLeaderboards[18].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[19] = tmpLeaderboards[19].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[20] = tmpLeaderboards[20].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[21] = tmpLeaderboards[21].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[22] = tmpLeaderboards[22].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[23] = tmpLeaderboards[23].OrderBy(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[24] = tmpLeaderboards[24].OrderBy(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[25] = tmpLeaderboards[25].OrderBy(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[26] = tmpLeaderboards[26].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[27] = tmpLeaderboards[27].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[28] = tmpLeaderboards[28].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[29] = tmpLeaderboards[29].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[30] = tmpLeaderboards[30].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			tmpLeaderboards[31] = tmpLeaderboards[31].OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
			#endregion

			#region Add
			leaderboards.Add(new Leaderboards() { category = "weightedPP", entries = tmpLeaderboards[0] });
			leaderboards.Add(new Leaderboards() { category = "rawPP", entries = tmpLeaderboards[1] });
			leaderboards.Add(new Leaderboards() { category = "weightedAverageScorePercentage", entries = tmpLeaderboards[2] });
			leaderboards.Add(new Leaderboards() { category = "averageScorePercentage", entries = tmpLeaderboards[3] });
			leaderboards.Add(new Leaderboards() { category = "weightedAverageRank", entries = tmpLeaderboards[4] });
			leaderboards.Add(new Leaderboards() { category = "averageRank", entries = tmpLeaderboards[5] });
			leaderboards.Add(new Leaderboards() { category = "weightedAverageCountryRank", entries = tmpLeaderboards[6] });
			leaderboards.Add(new Leaderboards() { category = "averageCountryRank", entries = tmpLeaderboards[7] });
			leaderboards.Add(new Leaderboards() { category = "topPP", entries = tmpLeaderboards[8] });
			leaderboards.Add(new Leaderboards() { category = "highest96", entries = tmpLeaderboards[9] });
			leaderboards.Add(new Leaderboards() { category = "highest97", entries = tmpLeaderboards[10] });
			leaderboards.Add(new Leaderboards() { category = "highest98", entries = tmpLeaderboards[11] });
			leaderboards.Add(new Leaderboards() { category = "highest99", entries = tmpLeaderboards[12] });
			leaderboards.Add(new Leaderboards() { category = "nbOfRankedDiffPlayed", entries = tmpLeaderboards[13] });
			leaderboards.Add(new Leaderboards() { category = "nbOf95", entries = tmpLeaderboards[14] });
			leaderboards.Add(new Leaderboards() { category = "bestRank", entries = tmpLeaderboards[15] });
			leaderboards.Add(new Leaderboards() { category = "nbOf325", entries = tmpLeaderboards[16] });
			leaderboards.Add(new Leaderboards() { category = "nbOfCountryFirst", entries = tmpLeaderboards[17] });
			leaderboards.Add(new Leaderboards() { category = "nbOfFirst", entries = tmpLeaderboards[18] });
			leaderboards.Add(new Leaderboards() { category = "nbOfTop10", entries = tmpLeaderboards[19] });
			leaderboards.Add(new Leaderboards() { category = "countryWeightedPPaverage", entries = tmpLeaderboards[20] });
			leaderboards.Add(new Leaderboards() { category = "countryRawPPAverage", entries = tmpLeaderboards[21] });
			leaderboards.Add(new Leaderboards() { category = "countryAverageScorePercentage", entries = tmpLeaderboards[22] });
			leaderboards.Add(new Leaderboards() { category = "countryAverageOfbestRanks", entries = tmpLeaderboards[23] });
			leaderboards.Add(new Leaderboards() { category = "countryWeightedRankAverage", entries = tmpLeaderboards[24] });
			leaderboards.Add(new Leaderboards() { category = "countryRankAverage", entries = tmpLeaderboards[25] });
			leaderboards.Add(new Leaderboards() { category = "countryWeightedAverageScorePercentage", entries = tmpLeaderboards[26] });
			leaderboards.Add(new Leaderboards() { category = "countryTopPPAverage", entries = tmpLeaderboards[27] });
			leaderboards.Add(new Leaderboards() { category = "countrySumOfRankedDiffPlayed", entries = tmpLeaderboards[28] });
			leaderboards.Add(new Leaderboards() { category = "countrySumOf95", entries = tmpLeaderboards[29] });
			leaderboards.Add(new Leaderboards() { category = "countrySumOf325", entries = tmpLeaderboards[30] });
			leaderboards.Add(new Leaderboards() { category = "countrySumOfTop10", entries = tmpLeaderboards[31] });
			#endregion

			db.DropCollection("leaderboards");
			dbCollectionLeaderboards.InsertMany(leaderboards);

			sp.Stop();
			Console.WriteLine("Leaderboards generated, took " + sp.Elapsed.TotalSeconds + "s");
			return leaderboards;
		}

		private static void GenerateSnapshot(List<Leaderboards> leaderboards)
		{
			Stopwatch sp = Stopwatch.StartNew();
			IMongoCollection<Snapshot> dbCollectionSnapshots = db.GetCollection<Snapshot>("snapshots");
			Snapshot s = dbCollectionSnapshots.Find(new BsonDocument().Add("date", DateTime.Today)).FirstOrDefault();

			// Check if you need a snapshot
			if (s != null)
			{
				Console.WriteLine("A snapshot has already been made today, skipping...");
			}
			else
			{
				List<Snapshot> snapshots = new List<Snapshot>();

				for (int i = 0; i < leaderboards.Count; i++)
				{
					snapshots.Add(new Snapshot(leaderboards[i]));
				}

				// All snapshots that are exactly 7 days older
				var filter = Builders<Snapshot>.Filter.Eq("date", DateTime.Today - new TimeSpan(7, 0, 0, 0));

				dbCollectionSnapshots.DeleteMany(filter);
				dbCollectionSnapshots.InsertMany(snapshots);
			}

			sp.Stop();
			Console.WriteLine("Snapshot generated, took " + sp.Elapsed.TotalSeconds + "s");
		}



		///////////////////////// Utilities	\\\\\\\\\\\\\\\\\\\\\\\\\

		private static T GetAsyncFromScoresaber<T>(string link, int page, string ssid = "")
		{
			link = link.Replace("<ssid>", ssid).Replace("<page>", page.ToString());
			string res = ""; 
			try
			{
				res = client.GetAsync(link).Result.Content.ReadAsStringAsync().Result;
				T response = JsonConvert.DeserializeObject<T>(res);
				return response;
			} catch (Exception e)
			{
				Console.WriteLine("\nAn exception has been catched when calling ScoreSaber : \n" + e.Message);
				Console.WriteLine(res);
				Thread.Sleep(15000);
				return GetAsyncFromScoresaber<T>(link, page, ssid);
			}
		}

		private static float GetTotalMultiplier(string modsStr)
		{
			float multiplier = 1;
			List<string> mods = modsStr.Split(',').ToList();

			if (mods.Contains("DA")) { multiplier += 0.02f; }
			if (mods.Contains("SS")) { multiplier -= 0.3f; }
			if (mods.Contains("FS")) { multiplier += 0.08f; }
			if (mods.Contains("SF")) { multiplier += 0.1f; }
			if (mods.Contains("GN")) { multiplier += 0.04f; }
			if (mods.Contains("NA")) { multiplier -= 0.3f; }
			if (mods.Contains("NB")) { multiplier -= 0.1f; }
			if (mods.Contains("NF")) { multiplier -= 0.5f; }
			if (mods.Contains("NO")) { multiplier -= 0.05f; }
			//if (_modifiers.zenMode) { multiplier -= 1f; modifiers.Add("ZM"); }

			return multiplier;
		}
	}
}
