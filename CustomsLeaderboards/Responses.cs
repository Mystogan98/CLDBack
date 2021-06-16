using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CustomsLeaderboards
{
	public class PlayerResponse
	{
		public string playerId, playerName, avatar, country;
	}

	public class PlayerResponseWrap
	{
		public List<PlayerResponse> players;
	}

	public class ScoreResponse
	{
		public int leaderboardId;
		public int unmodififiedScore, score, maxScore;
		public string mods;
		public DateTime timeSet;
		public double pp;
	}

	public class ScoreResponseWrap
	{
		public List<ScoreResponse> scores;
	}
}
