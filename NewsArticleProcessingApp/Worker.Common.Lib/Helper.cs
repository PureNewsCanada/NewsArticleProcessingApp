using System;

namespace Common.Lib
{
    public static class Helper
    {
        public  static string GetCountrySlug(string country)
        {
            return country.ToLower() switch
            {
                "usa" => "US",
                "canada" => "CA",
                "uk" => "GB",
                _ => string.Empty
            };
        }

        public static string GetProxyForCountry(string countrySlug,string username, string password)
        {
            return countrySlug switch
            {
                "US" => $"http://{username}:{password}@us.smartproxy.com:{new Random().Next(10001, 10011)}",
                "CA" => $"http://{username}:{password}@ca.smartproxy.com:{new Random().Next(20001, 20011)}",
                "GB" => $"http://{username}:{password}@gb.smartproxy.com:{new Random().Next(30001, 30011)}",
                _ => string.Empty
            };
        }
    }
}
