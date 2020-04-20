﻿using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using LunarLabs.Parser;
using Phantasma.Cryptography;
using Phantasma.Domain;
using Phantasma.SDK;
using Phantasma.Numerics;
using System.IO;
using Phantasma.Core.Types;

namespace Poltergeist
{
    public class WalletConnector : WalletLink
    {
        private static readonly string DefaultAvatar = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAABGBSURBVHhe7d3XrxxFEwXwIeecEUnknDFRZBkkEK888PfxDEgggjEiG0TOwYCJJieT88evP5/VeL3X3rvsnZndnSOVemJPd9Wp6jBph88///yfqsfCYsfNaY8FRU+ABUdPgAVHT4AFR0+ABUdPgAVHT4AFR0+ABUdPgAVHT4AFR0+ABUdPgAXHQhFghx122ELgn3/+fy9M+vfffw/WFwULQQBGJX/99ddII++4447VTjvttBVBIvOMhSAAA8fIUggJsg6LYPBhzD0BYlSGjnGHo8Gff/5ZZDgyLALm/oGQuvF///33Yug//vijCAIEu+yyS7X77ruXNEQJ5pkYM0mAuoEYN4aUZh8D//rrr9V3331Xffrpp9Vnn31WffXVV2U77697vHM0D2S33Xar9t9//+qoo46qDj/88GrfffctxLA9x0sRRSq/enSZNcwsARiR4gkj2IYAP/30U/X+++9X69evr7744ouyzvNj4O0ZilFDqJ133rkY/ogjjqhOOeWU6phjjqn22muvcoy87HdsyjKLmEkC1I2UCPDxxx9Xb7zxRvXuu+9WmzZtKtsYqG5w542DnON44hoMLhIce+yx1ZlnnlkdeeSRJQqIJEiS8swaZjYCEIZh9KeeeqqE+HiifZYZDwlgXAM5P4YnIC8EsC5veYoKq1atqk444YTBvlnETBAgHsmIdY9n+A0bNhQDpG2P1zOI5RBhXMTIyYO4JsjL9qxLjzvuuOqiiy4qzUPOSRoyQbZ3DTNDgChPm/7yyy9XzzzzTFmGGL4p1K+HjJoG0eC8886rdt111wEhNQ2ORQRNRYjTJcwEAXTi9thjj+rLL7+s1qxZU9p57W/AGBQeb1tpxLsDy8p40kknVatXr64OPPDAQbRAEMtI0UeACcF79OwffPDBMqwDRqd0HkfBTSKGTWRKeFdOQ0gkOP3006tffvllMHx0fFMEXQ46TwBKE/IfffTR6rffftvCo6JQ26w3hRhb6rqWpcohEu23337VZZddVpqFDEFzXtfQuUZJOI2iLD///PPVQw89VP38888DT7I/qWPq4bgJKEfKmeVs5/E8H2H1UxKhmi7juOhcqXhTPPrVV1+tHnnkkcFET5QdAdtCmCbhmrlu0rqnk7Vr15bohQTK2UV0rglIONXRu+eee4rnd9V7tgUk1lHl/TfffHOZPOoiCTqnWSH0X1KW3j7jQzxqlgSJ9Vng4YcfLhNVXUTrBKCsOkQAvf3vv/++9PRn0fsDoV/99Anuu+++0iEEkaAr0aB17cbAIcK6deuqDz/8sChIGJ1VKL/wrw5I7Y6kmctsyzFto1UCxMghwUcffVQ9+eSTxXOCrnjKNPD4449Xn3zySamv6DYc/dpA6xFAWKQIPX1DJx7CY6QBEsyajIJ+gSigSUD8hScABWTo9Nprr1UbN24sy/GQKNNxsyajoC4ffPBB9eabbw7W20brBBDu9fafe+65YniEkNqHBPU5/1lCnQwRdWN0k1uiwMITgAKE+nfeeaf69ttvB+E/4VGaDtMsYpgAqY/7Ge+9915PAODtL774YlFMFCKdR1FXguSmidP8tYnWCeAWb3rGkN7xPEqaNqlhobq3jdYJ8MorrxTvF/7JLIf87cHMoNGOOvJ+9zraRqMESCiMsQ0B3efnFdZ5/zxD/YgIQA/mPejAcpygaTRKgIR5Fda7/+GHH8q8PwJQjO2LhK+//rr65ptvyrKI4KknumgSrTQBaQs9tx/W1yd/EGERxDON+gGJgMT2JtEoAYQ5lcV2baEne8P4OvMtL4KkM8jwdMMJmkajBFDpQGWFf4y3Pe3jIoEj1N9nSBPZJBq9ogom/Km02TBKsD7Pvf9tgQ44QxvGh0avmvaNx3txUzMQQiyqGAXQBVhvGo0TgGA75udZv0UVoAMEsL4wBFBZzG+j09M10EFuibeBRgmgvVdR7X0bbO8aREK6yPMBbaDxnkdCnbQt1ncJdNBWBxAabwIyEkg0sLzIQg9mRdtyhtYJsMhSJ0BbaJwA9YqHDHVZJKgvHXgXInppGo0TIJX2Tn1emRqWRQGjm/2ki7bQOAEMe3j/3nvvXe5+jcIwIeZVOMKee+5ZHCHTwU2j0SsmzBnyaPcOOuigzXu2hOMWQeiCDhCATmxrGo0SAMNV0uyXSHDooYdu3rOYoAMEEBHbQuMxBwEQAft9WClhz3r2zSvUrz7ho/33Qco20Yq2KYL46OI+++xTjJ5hISLMK2J8qfr6kohPymS9DbRGAEbXAfKZNcvxfMpAgnkUMORTV+H/sMMOK5+ibasDCI1eNUoAJNARPOuss4oCiP2JDvModairD0lpBhhf/evNQ1NolABRQl0ZPshsHBwvGVbUPIGB8xyEpu/4448v2xCg7hxNopW4E2On7Tv//PMHj4PNMwFA1FPX0047rSzH8EjQRt3baXg2Q+Up49xzzx1MClFCCDJvklDP+31VtAtkb5UAFKAzpDesPYyi5hUiHgKoq6+JttHmD6NVAgQUIQoccsghZRkx5lE0dyZ+dHzVswtk7wwBjId9dZuSKCZKm1Uoe+oh5fmagLPPPruQwHJbY/86WicABaUDePLJJ5dRgc5RwqMmYhaR9h7URZ2M+3V4rZMu1K31PgBFxVN0jm666aZycwQpKHCpW8ZdF28CqxuoA2+/4YYbSmo7Aqhz22iVABRFEQxNGZ6OFR6vvvrqgYJ4iXTWpD7Es37llVeWPg4ygHThI0C8hVfE4FJj5AsvvHCgyCDHdxXD5UtT5jOxQn/6AVLGNy3cNlpvAgilZVnoR4grrriiOuOMM8qsWY6Ncim1a1C2RDKSW976Nepiv7rZpn6O6UI9WiXAKOQlCUq67rrrBvMDFJrtaVu7BMZVLmVUXsunnnpqdcsttwwef+siOve1cAqkPHPmlCa9//77y7f1hFQE4TldI4EyIUFSfxPT6VPmen+ga+gkAQLLxJszvrjtF3F5kbJ+XBcQUopQota1115bbneLXPXI0DV0jgBRUt3Dta0U/NJLL1VPPPHE4GVK2wLHpw0OpkWSlCn5uU46eCmHY2y76qqrqnPOOadEAdsQwvFdi1hB5/8ZBDpOlEuZviR+7733lm/rRLHxvrqHTcv4MEwAqW3EtZXDn0RvvPHGkoYU0yzDSmEmCMCbKDkGFwFeeOGF6umnny7LaXvtp/SVUnw93xBAmDfEc3fP0z1CPrIqyyxgJghQ93ApQ5hp8zdwRPB7Gd8bTrgllqdBBNcMkp9tlg1TGd8UrzIyPNTL23XMDAGAUi2nfWVoqS9t+QCzbw6LCAwhRYJpIIaUn7w9zXzxxReXJ3oRgdcnCllPOgtonQCMClFywng8iIyjTMf4CLNo4NPz+fiSoaQ8DB9DCAbK9ZxXz98+x4Vs+h/yYGyTOv4e7s7lpOSSr+vJuwtEaZ0ADMFQFEOplMLLGCzKirG2h3QWHY8MPsHma5y+ReybfJoJ+dtfF9fItZxPPKTiD+HC+9FHH10eYTehE+NNgpA71wPXbxOtEiCK0J4LodYZ0bYYZFxlx1MdT5wf5VK8MO2T9IjlelLnOMa1nSvVkWNsnbsYSTmCkGYShOjySz65RlvoBAESnvPhKHfNeKybJfHY7cFxDCg/hqVs61E0iSHraYyR6yxlEPsg504CdXN+SPBf8poWVpwAoyoZD2Uk4dY3g03y+MumMO2ZAFOpkOMQJEqLMerItlEGHDae9XoeyZcoG4EYKuQg2TYM2+Lh4FgEtM0+P4jwF9SDDz64uuCCC8oLMSFq4JxRea8kGokAqZTQqx2NUkzmMLwefJoBSpG6hepmkGXGj3LqxukS1EkzIpopZ5YR2t/CkFvZEd6x3gm45JJLBiMJUKc6IZpAY00AAwKDUoy/ZzG+djneHVinDL1tw608RAm2yyv5dQnqkHogsnsXfoOnQ5oyMzAdADK4Yygi6HDCXBGAMlJxBmTYf69XbuzonVOSYxgVKaR1IlgHD4hcfvnlJXxSUP2YrkCZlI1R1dFPsAxHtfvqzujD9bTdsk7npZdeWoju/Cax4gRQyRDA30H8Cj7hXuXrSsk5lGY9od9+vXKeYubNEE1+XYLy6MSamdSkbdq0qRgWyeMEEKJI00yoK52sWrWquuaaa8r2pjA1AqiQitR7ukRlKeaBBx4o9/TtWy7kiTDyMUwz7+6WqzF6vC4kk7pGXerKD7ItKWQ5ZbceoiVNc5UZR8f474HfvzA+gyqr4x27XPhoxvXXX19uKslH/im3ZfVVtmlhagRIhSkgX73Cfm38XXfdVZRUN8By4DwVpxBkoAivkulAIYIRg28OOYbESCFEjOd8sC0pSbmS5rwQIefbb11q5OJ3N2+99Vb5GSRCqHfKkLyWC9cV7TwY6x2C6BXo07Jtk+Y/jKkRQKWBVygouFlz9913lxm5eMskCOPrxpVXoo3OomGVaVpCgTFs3fjOz/ZRCqxvH1ayc/3R3H0HP7owrFM/5VG+GD2EmdRIqaPyrl69utxwklf6BpPqcClMjQAqHqNowyjJo1ymYa3bFyMsF/IMCaJsaRQewrkG4wujXjARRjUTBxxwwBbEGRZ5RCg7eRqm+q+Puvyrp3J/wQSVc9RFfo6HurEnMXwg39zIsiwSGC4aTop60Z8yTANTI4ACRRnC/Z133lmGP/WODkyinJzDKJRSRww4iljKQ5RBh8w7B8I0MR8hL/vlqxOGaPoZOnDKHuJFUo7ha9muHClLfXkSODfXkoenjPI2sX3DOvgvmCoBFPDHH3+sbr/99qJE27qEcaJP3dBdAYObHT3xxBNLZJgmptoECFN33HFHGePzrK4pUnnqZULQrpF0GMqrjPoAt912W2neEmWmgakRANasWVP+iavQ02bqNDCKkMPb0ox1CZoiBPBNgVtvvbUMhadFgKkNKNetW1c9++yzJVwx/jjhtmko0/aki0g/St/KA7Ei7bQwEQHq7ONBxsGMD/aRUd7WNoTOYVHOunQR9IkEnIuuPQwbHWcENCkmIoALpyeq97x27doyPLI9BOixMqBbU80efQPE+C+YuAlgeDD9udRv0EOGXqYr4NkCTYFmK9smwcRNgHG0qdDHHntsydBpey/TF85nnsKt5v/abxmbAHWW6eSZFtUW6ZwoxKiC9lgZ6Lto+zUFZifrfZnlYlkEcCHDJMtvv/12Ge8bnuTCKcSkhekxHmILOuaEyMAuGcLaX3fYbWEsArhYIGPeb7yfi9pPcuFeVlYYPsbWGfRCjG32hRh1m20LYx2VzAmP92CHqV7Msw/s69EMGBcBcutdX0C/QNMcEoyLsY40ExXW8X4POEIKQlzYMb2svEAm3Ny8Mgp7/fXXBzaQjts5HIsAYZRMXYj3W852aQrWoxnE2MABDcdFAcu2jzsVP3ascEEXcKE6lhNuekwHbBHQP/HswoYNGwoBROz6MdvC2BFA26/n7wEJmffSHQl0zMf1/GBs9zXVa9zpqRQhppfuCHDSjRs3li+ocNZxMRYBtPcei/IMnGYgF+3RHegMIoEmmr3qkWFbGDsCCP8wHHZ6dAccUwQwUhvXSccigF6/jA09sCwk6KU7Iuzn2UZPLE+VAOvXry+sytiybwK6BfZAAs6JBF5J82TxOBiLALn3LPPARXvpjmQyDozUDAvHwVYESEaZUND79ygSWIdcqEc3wB4hAZgiNieQIWGdHMPYigBOyjSjE7X92hUIAZL26BbYJc7rhZzcq4mMwlYEcGC8X2buNNVDP9jeS7dkGF7HSzPAmccmAOMzuJTneyVqGGFUL92QUdAJFAXYkYwiCYyMABjjBG/5eLt3qYv06AZG2ceIIH23ZRPAwVJTizoUlmUY6QnRPdTtQzJ7G1vlKe5hbEWAOlM8b+aBgx6zB4Y3gefOoAiwFJYkgBPzuLdtvcyWIIDHxjXhIsJSJBjZBIAOoBsMeRqol9kSYHRObJp4bAKEPQw/7nRij+6BHXXm3cG1PBpV9T8nZP2i8jt6HwAAAABJRU5ErkJggg==";

        public PhantasmaAPI api => AccountManager.Instance.phantasmaApi;

        private string _WIF;
        private PhantasmaKeys _keys;
        protected override WalletStatus Status => AccountManager.Instance.CurrentState != null ? WalletStatus.Ready : WalletStatus.Closed;

        protected override Account GetAccount()
        {
            var account = AccountManager.Instance.CurrentAccount;

            var state = AccountManager.Instance.CurrentState;

            if (state != null)
            {

                var balances = state.balances.Select(x => new Balance()
                {
                    symbol = x.Symbol,
                    value = UnitConversion.ToBigInteger(x.Available, x.Decimals).ToString(),
                    decimals = x.Decimals,
                });

                return new Account()
                {
                    name = account.name,
                    id = account.name,
                    address = state.address,
                    balances = balances.ToArray(),
                    avatar = DefaultAvatar
                };
            }

            // TODO should return an error to plink here!!!
            Log.WriteError("not logged in, pls implement this case!");

            return new Account()
            {
                name = "Unknown",
                id = "unknown",
                address = Address.Null.Text,
                balances = new Balance[0],
                avatar = DefaultAvatar
            };
        }

        protected override PhantasmaKeys Keys {
            get
            {
                var currentWIF = AccountManager.Instance.CurrentAccount.WIF;
                if (currentWIF != _WIF)
                {
                    _WIF = currentWIF;
                    _keys = PhantasmaKeys.FromWIF(_WIF);
                }

                return _keys;
            }
        }

        public WalletConnector() : base("Poltergeist")
        {
        }

        protected override void InvokeScript(byte[] script, int id, Action<byte[], string> callback)
        {
            WalletGUI.Instance.CallOnUIThread(() =>
            {
                api.InvokeRawScript("main", Base16.Encode(script), (x) =>
                {
                    callback(Base16.Decode(x.result), null);

                }, (error, log) =>
                {
                    callback(null, log);
                });
            });
        }

        protected override void SignTransaction(string nexus, string chain, byte[] script, byte[] payload, int id, Action<Hash, string> callback)
        {
            var state = AccountManager.Instance.CurrentState;
            if (state == null)
            {
                callback(Hash.Null, "not logged in");
                return;
            }

            var expectedNexus = AccountManager.Instance.Settings.nexusName;
            if (nexus != expectedNexus)
            {
                callback(Hash.Null, "nexus mismatch, expected: " + expectedNexus);
                return;
            }

            var account = AccountManager.Instance.CurrentAccount;

            if (account.platforms.HasFlag(PlatformKind.Phantasma))
            {
                WalletGUI.Instance.CallOnUIThread(() =>
                {
                var description = DescriptionUtils.GetDescription(script);
                    WalletGUI.Instance.Prompt("Allow dapp to send a transaction on your behalf?\n" + description, (success) =>
                      {
                          if (success)
                          {
                              // TODO show description of transfer :^)              
                              WalletGUI.Instance.SendTransaction(description, script, payload, chain, (hash) =>
                              {
                                  if (hash != Hash.Null)
                                  {
                                      callback(hash, null);
                                  }
                                  else
                                  {
                                      callback(Hash.Null, "something bad happend while sending");
                                  }
                              });
                          }
                          else
                          {
                              callback(Hash.Null, "user rejected");
                          }
                      });

                });

            }
            else
            {
                callback(Hash.Null, "current account does not support Phantasma platform");
            }
        }

        protected override void Authorize(string dapp, Action<bool, string> callback)
        {
            var state = AccountManager.Instance.CurrentState;
            if (state == null)
            {
                callback(false, "not logged in");
                return;
            }

            WalletGUI.Instance.CallOnUIThread(() =>
            {
                WalletGUI.Instance.Prompt($"Give access to dapp \"{dapp}\" to your \"{state.name}\" account?", (result) =>
               {
                   callback(result, "rejected");
               });
           });

        }
    }
}