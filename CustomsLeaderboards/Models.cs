using MongoDB.Bson;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CustomsLeaderboards
{
	public class Leaderboards
	{
		public ObjectId _id;
		public string category;
		public Dictionary<string, double> entries;
	}

	public class Snapshot
	{
		public ObjectId _id;
		public DateTime date;
		public string category;
		public Dictionary<string, double> entries;

		public Snapshot(Leaderboards ld)
		{
			date = DateTime.Today;
			category = ld.category;
			entries = ld.entries;
		}
	}

	public class CountryData
	{
		public ObjectId _id;
		public string country;
		public double weightedPPaverage, rawPPAverage, averageScorePercentage, averageOfbestRanks, weightedRankAverage, rankAverage, weightedAverageScorePercentage, topPPAverage;
		public int sumOfRankedDiffPlayed, sumOf95, sumOf325, sumOfTop10;
	}

	public class ProfileData
	{
		public ObjectId _id;
		public string SSID;
		public double weightedPP, rawPP, averageScorePercentage, weightedAverageRank, averageRank, weightedAverageCountryRank, averageCountryRank, weightedAverageScorePercentage, topPP, highest96, highest97, highest98, highest99;
		public int nbOfRankedDiffPlayed, nbOf95, bestRank, nbOf325, nbOfCountryFirst, nbOfFirst, nbOfTop10;
	}

	public class Profile
	{
		public ObjectId _id;
		public string SSID, country, nickname, avatarPath;
		public DateTime last;
	}

	public class Map
	{
		public ObjectId _id;
		public string LDID;
		public int maxScore;
	}

	public class Score
	{
		public ObjectId _id;
		public string SSID, LDID;
		public int score, rank, countryRank;
		public double pp;
		public DateTime timeSet;
	}
}
