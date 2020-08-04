using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using ArchiSteamFarm;
using ArchiSteamFarm.Collections;
using ArchiSteamFarm.Json;
using ArchiSteamFarm.Plugins;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ItemDispenser {

	internal class IPAddressConverter : JsonConverter {

		public override bool CanConvert(Type objectType) {
			return (objectType == typeof(IPAddress));
		}

		public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) {
			writer.WriteValue(value.ToString());
		}

		public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) {
			return IPAddress.Parse((string) reader.Value);
		}
	}

	internal class IPEndPointConverter : JsonConverter {

		public override bool CanConvert(Type objectType) {
			return (objectType == typeof(IPEndPoint));
		}

		public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) {
			IPEndPoint ep = (IPEndPoint) value;
			JObject jo = new JObject();
			jo.Add("Address", JToken.FromObject(ep.Address, serializer));
			jo.Add("Port", ep.Port);
			jo.WriteTo(writer);
		}

		public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) {
			JObject jo = JObject.Load(reader);
			IPAddress address = jo["Address"].ToObject<IPAddress>(serializer);
			int port = (int) jo["Port"];
			return new IPEndPoint(address, port);
		}
	}

	[Export(typeof(IPlugin))]
	public class ItemDispenser : IBotTradeOffer, IBotModules {
		private readonly ConcurrentDictionary<Bot, ConcurrentHashSet<DispenseItem>> BotSettings = new ConcurrentDictionary<Bot, ConcurrentHashSet<DispenseItem>>();
		private List<ulong> alreadyGifted = new List<ulong>();

		public string Name => nameof(ItemDispenser);

		public Version Version => typeof(ItemDispenser).Assembly.GetName().Version;

		public async Task<bool> OnBotTradeOffer([NotNull] Bot bot, [NotNull] Steam.TradeOffer tradeOffer) {
			if (tradeOffer == null) {
				ASF.ArchiLogger.LogNullError(nameof(tradeOffer));
				return false;
			}

			if (alreadyGifted.Contains(tradeOffer.OtherSteamID64)) {
				return false;
			}
			alreadyGifted.Add(tradeOffer.OtherSteamID64);

			if (tradeOffer.ItemsToGiveReadOnly.Count > 1) {
				return false;
			}

			//If we receiveing something in return, and donations is not accepted - ignore.
			if (tradeOffer.ItemsToReceiveReadOnly.Count > 0 && !bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.AcceptDonations)) {
				return false;
			}
			byte? holdDuration = await bot.GetTradeHoldDuration(tradeOffer.OtherSteamID64, tradeOffer.TradeOfferID).ConfigureAwait(false);

			if (!holdDuration.HasValue) {
				// If we can't get trade hold duration, ignore
				return false;
			}

			// If user has a trade hold, we add extra logic
			if (holdDuration.Value > 0) {
				// If trade hold duration exceeds our max, or user asks for cards with short lifespan, reject the trade
				if ((holdDuration.Value > ASF.GlobalConfig.MaxTradeHoldDuration) || tradeOffer.ItemsToGiveReadOnly.Any(item => ((item.Type == Steam.Asset.EType.FoilTradingCard) || (item.Type == Steam.Asset.EType.TradingCard)) && CardsFarmer.SalesBlacklist.Contains(item.RealAppID))) {
					return false;
				}
			}

			//if we can't get settings for this bot for some reason - ignore
			if (!BotSettings.TryGetValue(bot, out ConcurrentHashSet<DispenseItem> ItemsToDispense)) {
				return false;
			}
			StringBuilder s = new StringBuilder();
			foreach (Steam.Asset item in tradeOffer.ItemsToGiveReadOnly) {
				if (!ItemsToDispense.Any(sample =>
									   (sample.AppID == item.AppID) &&
									   (sample.ContextID == item.ContextID) &&
									   ((sample.Types.Count > 0) ? sample.Types.Any(type => type == item.Type) : true)
										)) {
					return false;
				} else {
					// item.AdditionalProperties.
				}
			}
			ASF.ArchiLogger.LogGenericInfo(JsonConvert.SerializeObject(tradeOffer, (Formatting.Indented), new JsonSerializerSettings() { ReferenceLoopHandling = ReferenceLoopHandling.Ignore, DateFormatString = "yyyy-MM-dd hh:mm:ss" }));
			return true;
		}

		public void OnLoaded() => ASF.ArchiLogger.LogGenericInfo("Item Dispenser Plugin by Ryzhehvost, powered by ginger cats");

		public void OnBotInitModules([NotNull] Bot bot, [CanBeNull] IReadOnlyDictionary<string, JToken> additionalConfigProperties = null) {
			if (additionalConfigProperties == null) {
				BotSettings.AddOrUpdate(bot, new ConcurrentHashSet<DispenseItem>(), (k, v) => new ConcurrentHashSet<DispenseItem>());
				return;
			}

			if (!additionalConfigProperties.TryGetValue("Ryzhehvost.DispenseItems", out JToken jToken)) {
				BotSettings.AddOrUpdate(bot, new ConcurrentHashSet<DispenseItem>(), (k, v) => new ConcurrentHashSet<DispenseItem>());
				return;
			}

			ConcurrentHashSet<DispenseItem> dispenseItems;
			try {
				dispenseItems = jToken.Value<JArray>().ToObject<ConcurrentHashSet<DispenseItem>>();
				BotSettings.AddOrUpdate(bot, dispenseItems, (k, v) => dispenseItems);
			} catch {
				ASF.ArchiLogger.LogGenericError("Item Dispenser configuration is wrong!");
				BotSettings.AddOrUpdate(bot, new ConcurrentHashSet<DispenseItem>(), (k, v) => new ConcurrentHashSet<DispenseItem>());
			}
			return;
		}
	}
}
