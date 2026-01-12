using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace OnlineFlightTicket
{
    // =========================
    // AYARLAR (BURAYI DOLDUR)
    // =========================
    static class AppSettings
    {
        // E-posta göndermek istiyorsan doldur:
        public const string GmailFromAddress = "";      // örn: airlineskaya@gmail.com
        public const string GmailAppPassword = "";      // Gmail uygulama şifresi (16 karakterli)
        public const bool EnableEmail = false;          // true yaparsan mail atar

        // Hava durumu göstermek istiyorsan doldur:
        public const string OpenWeatherApiKey = "";     // OpenWeather API key
        public const bool EnableWeather = false;        // true yaparsan hava durumu çeker
    }

    class Ucus
    {
        public string Havayolu { get; set; }
        public string Marka { get; set; }
        public string Model { get; set; }
        public string Kalkis { get; set; }
        public string Varis { get; set; }
        public DateTime Tarih { get; set; }

        // BosKoltuk = toplam boş koltuk. Satın alındıkça azalır.
        public int BosKoltuk { get; private set; }

        public decimal Fiyat { get; set; }

        // Rezervasyon yapan kullanıcı adları (koltuk ayırma)
        public List<string> Rezervasyonlar { get; private set; } = new();

        public Ucus(string havayolu, string marka, string model, string kalkis, string varis, DateTime tarih, int bosKoltuk, decimal fiyat)
        {
            Havayolu = havayolu;
            Marka = marka;
            Model = model;
            Kalkis = kalkis;
            Varis = varis;
            Tarih = tarih;
            BosKoltuk = bosKoltuk;
            Fiyat = fiyat;
        }

        public string Bilgi()
        {
            // Rezervasyonlar koltuk ayırdığı için “satılabilir” koltuk = BosKoltuk - Rezervasyonlar.Count
            int satilabilir = Math.Max(0, BosKoltuk - Rezervasyonlar.Count);

            return $"{Havayolu} ({Marka} {Model}): {Kalkis} -> {Varis}, Tarih: {Tarih:dd.MM.yyyy HH:mm}, " +
                   $"Boş Koltuk: {BosKoltuk}, Rezervasyon: {Rezervasyonlar.Count}, Satılabilir: {satilabilir}, Fiyat: {Fiyat:C}";
        }

        public bool RezervasyonYap(string kullaniciAdi)
        {
            if (Tarih <= DateTime.Now) return false;
            if (Rezervasyonlar.Contains(kullaniciAdi)) return false;

            // Rezervasyon, mevcut boş koltuklardan birini “ayırır”.
            if (Rezervasyonlar.Count < BosKoltuk)
            {
                Rezervasyonlar.Add(kullaniciAdi);
                return true;
            }
            return false;
        }

        public bool RezervasyonuIptalEt(string kullaniciAdi)
        {
            return Rezervasyonlar.Remove(kullaniciAdi);
        }

        public bool RezervasyonuSat(string kullaniciAdi)
        {
            // Rezervasyon bilete çevrilince:
            // 1) rezervasyon listeden çıkar
            // 2) BosKoltuk 1 azalır (satış gerçekleşti)
            if (!Rezervasyonlar.Contains(kullaniciAdi)) return false;
            if (BosKoltuk <= 0) return false;

            Rezervasyonlar.Remove(kullaniciAdi);
            BosKoltuk -= 1;
            return true;
        }

        public bool DirektBiletSat()
        {
            // Rezervasyonsuz direkt satış: satılabilir koltuk var mı?
            int satilabilir = BosKoltuk - Rezervasyonlar.Count;
            if (satilabilir <= 0) return false;

            BosKoltuk -= 1;
            return true;
        }
    }

    class Kullanici
    {
        public string KullaniciAdi { get; set; }
        public string Gmail { get; set; }
        public string TCKimlik { get; set; }
        public string SifreHash { get; set; }

        public List<Ucus> SatinAlinanUcuslar { get; private set; } = new();
        public List<Ucus> Rezervasyonlar { get; private set; } = new();

        public Kullanici(string kullaniciAdi, string gmail, string sifre, string tcKimlik)
        {
            if (!TCKimlikKontrol(tcKimlik))
                throw new ArgumentException("T.C. Kimlik numarası 11 haneli olmalı ve geçerli bir formatta olmalı.");

            KullaniciAdi = kullaniciAdi;
            Gmail = gmail;
            TCKimlik = tcKimlik;
            SifreHash = SifreyiHashle(sifre);
        }

        public static bool TCKimlikKontrol(string tcKimlik)
        {
            return tcKimlik.Length == 11 && tcKimlik.All(char.IsDigit) && tcKimlik[0] != '0';
        }

        public static string SifreyiHashle(string sifre)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sifre));
            return Convert.ToBase64String(bytes);
        }

        public bool SifreDogrula(string sifre)
        {
            return SifreyiHashle(sifre) == SifreHash;
        }

        public async Task<string> RezervasyonYapAsync(Ucus ucus)
        {
            if (ucus.Tarih <= DateTime.Now)
                return "Rezervasyon başarısız: Uçuş tarihi gelecekte olmalı.";

            if (!ucus.RezervasyonYap(KullaniciAdi))
                return "Rezervasyon başarısız: Koltuk yok / zaten rezervasyon var.";

            Rezervasyonlar.Add(ucus);

            string weather = await WeatherService.GetWeatherAsync(ucus.Varis);
            string pnr = Helpers.GeneratePNR();

            NotificationService.Notify(
                subject: "Rezervasyon yapıldı",
                toEmail: Gmail,
                kullaniciAdi: KullaniciAdi,
                gidisTarihi: ucus.Tarih.ToString("dd.MM.yyyy HH:mm"),
                varisSehri: ucus.Varis,
                pnrKodu: pnr,
                weather: weather,
                fiyat: ucus.Fiyat,
                tcKimlik: TCKimlik
            );

            return $"✅ Rezervasyon yapıldı: {ucus.Kalkis} -> {ucus.Varis}, {ucus.Tarih:dd.MM.yyyy HH:mm} | Hava: {weather} | PNR: {pnr}";
        }

        public async Task<string> RezervasyonuSatAsync(Ucus ucus)
        {
            if (!ucus.RezervasyonuSat(KullaniciAdi))
                return "Satın alma başarısız: Rezervasyon yok veya koltuk kalmadı.";

            Rezervasyonlar.Remove(ucus);
            SatinAlinanUcuslar.Add(ucus);

            string weather = await WeatherService.GetWeatherAsync(ucus.Varis);
            string pnr = Helpers.GeneratePNR();

            NotificationService.Notify(
                subject: "Bilet satın alındı",
                toEmail: Gmail,
                kullaniciAdi: KullaniciAdi,
                gidisTarihi: ucus.Tarih.ToString("dd.MM.yyyy HH:mm"),
                varisSehri: ucus.Varis,
                pnrKodu: pnr,
                weather: weather,
                fiyat: ucus.Fiyat,
                tcKimlik: TCKimlik
            );

            return $"✅ Bilet satın alındı: {ucus.Kalkis} -> {ucus.Varis}, {ucus.Tarih:dd.MM.yyyy HH:mm} | Hava: {weather} | PNR: {pnr}";
        }

        public async Task<string> DirektBiletAlAsync(Ucus ucus)
        {
            if (ucus.Tarih <= DateTime.Now)
                return "Satın alma başarısız: Uçuş tarihi geçmiş.";

            if (!ucus.DirektBiletSat())
                return "Satın alma başarısız: Satılabilir koltuk yok.";

            SatinAlinanUcuslar.Add(ucus);

            string weather = await WeatherService.GetWeatherAsync(ucus.Varis);
            string pnr = Helpers.GeneratePNR();

            NotificationService.Notify(
                subject: "Bilet satın alındı (Direkt)",
                toEmail: Gmail,
                kullaniciAdi: KullaniciAdi,
                gidisTarihi: ucus.Tarih.ToString("dd.MM.yyyy HH:mm"),
                varisSehri: ucus.Varis,
                pnrKodu: pnr,
                weather: weather,
                fiyat: ucus.Fiyat,
                tcKimlik: TCKimlik
            );

            return $"✅ Direkt bilet alındı: {ucus.Kalkis} -> {ucus.Varis}, {ucus.Tarih:dd.MM.yyyy HH:mm} | Hava: {weather} | PNR: {pnr}";
        }
    }

    class SistemYoneticisi
    {
        public List<Ucus> Ucuslar { get; private set; } = new();
        public List<Kullanici> Kullanicilar { get; private set; } = new();

        public void UcusEkle(Ucus ucus) => Ucuslar.Add(ucus);
        public void KullaniciKaydet(Kullanici kullanici) => Kullanicilar.Add(kullanici);

        public Kullanici GirisYap(string kullaniciAdi, string sifre)
        {
            var kullanici = Kullanicilar.FirstOrDefault(k => k.KullaniciAdi == kullaniciAdi);
            return kullanici != null && kullanici.SifreDogrula(sifre) ? kullanici : null;
        }

        public void UcuslariListele()
        {
            Console.WriteLine("\nMevcut Uçuşlar:");
            for (int i = 0; i < Ucuslar.Count; i++)
                Console.WriteLine($"{i + 1}. {Ucuslar[i].Bilgi()}");
        }
    }

    static class Helpers
    {
        public static string GeneratePNR()
        {
            var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, 6).Select(s => s[random.Next(s.Length)]).ToArray());
        }

        public static int ReadInt(string prompt, int min, int max)
        {
            while (true)
            {
                Console.Write(prompt);
                var input = Console.ReadLine();
                if (int.TryParse(input, out int val) && val >= min && val <= max) return val;
                Console.WriteLine($"Geçersiz giriş. {min}-{max} arası sayı gir.");
            }
        }

        public static string ReadNonEmpty(string prompt)
        {
            while (true)
            {
                Console.Write(prompt);
                string s = Console.ReadLine() ?? "";
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                Console.WriteLine("Boş bırakılamaz.");
            }
        }
    }

    static class NotificationService
    {
        public static void Notify(string subject, string toEmail, string kullaniciAdi, string gidisTarihi, string varisSehri,
                                  string pnrKodu, string weather, decimal fiyat, string tcKimlik)
        {
            // Ayarlar kapalıysa sadece konsola yazalım
            if (!AppSettings.EnableEmail || string.IsNullOrWhiteSpace(AppSettings.GmailFromAddress) || string.IsNullOrWhiteSpace(AppSettings.GmailAppPassword))
            {
                Console.WriteLine($"[BİLDİRİM] {subject} | {kullaniciAdi} | {gidisTarihi} | {varisSehri} | PNR:{pnrKodu} | {weather} | {fiyat:C}");
                return;
            }

            try
            {
                var fromAddress = new MailAddress(AppSettings.GmailFromAddress, "Kaya Airlines");
                var toAddress = new MailAddress(toEmail, kullaniciAdi);

                using var smtp = new SmtpClient
                {
                    Host = "smtp.gmail.com",
                    Port = 587,
                    EnableSsl = true,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(fromAddress.Address, AppSettings.GmailAppPassword)
                };

                string htmlContent = $@"
<html>
<head>
<style>
body {{ font-family: Arial, sans-serif; background-color: #f9f9f9; }}
.container {{ max-width: 600px; margin: auto; padding: 20px; background-color: white; border: 1px solid #ddd; border-radius: 5px; }}
.pnr {{ color: red; font-weight: bold; }}
</style>
</head>
<body>
<div class='container'>
<h2>Kaya Airlines</h2>
<p>Sayın {kullaniciAdi},</p>
<ul>
<li>T.C. Kimlik: {tcKimlik}</li>
<li>Gidiş: {gidisTarihi}</li>
<li>Varış: {varisSehri}</li>
<li>Fiyat: {fiyat:C}</li>
<li>Hava: {weather}</li>
</ul>
<p class='pnr'>PNR: {pnrKodu}</p>
<p>İyi yolculuklar!</p>
</div>
</body>
</html>";

                using var emailMessage = new MailMessage(fromAddress, toAddress)
                {
                    Subject = subject,
                    Body = htmlContent,
                    IsBodyHtml = true
                };

                smtp.Send(emailMessage);
                Console.WriteLine("[E-posta gönderildi]");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[E-posta hatası]: {ex.Message}");
            }
        }
    }

    static class WeatherService
    {
        private static readonly HttpClient _client = new HttpClient();

        public static async Task<string> GetWeatherAsync(string city)
        {
            if (!AppSettings.EnableWeather || string.IsNullOrWhiteSpace(AppSettings.OpenWeatherApiKey))
                return "Hava durumu kapalı";

            try
            {
                string url = $"https://api.openweathermap.org/data/2.5/weather?q={Uri.EscapeDataString(city)}&appid={AppSettings.OpenWeatherApiKey}&units=metric&lang=tr";
                var json = await _client.GetStringAsync(url);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string description = root.GetProperty("weather")[0].GetProperty("description").GetString() ?? "bilgi yok";
                double temp = root.GetProperty("main").GetProperty("temp").GetDouble();

                return $"{description}, {temp:0.#}°C";
            }
            catch
            {
                return "Hava durumu alınamadı";
            }
        }
    }

    class Program
    {
        static async Task Main()
        {
            var sistemYoneticisi = new SistemYoneticisi();

            // Tarihler geçmiş olmasın diye: bugün + X gün
            DateTime baseDay = DateTime.Now.Date.AddDays(1);

            sistemYoneticisi.UcusEkle(new Ucus("THY", "Boeing", "737", "Istanbul", "Ankara", baseDay.AddHours(10), 20, 350.50m));
            sistemYoneticisi.UcusEkle(new Ucus("Pegasus", "Airbus", "A320", "Istanbul", "Izmir", baseDay.AddHours(14).AddMinutes(30), 25, 300.00m));
            sistemYoneticisi.UcusEkle(new Ucus("SunExpress", "Embraer", "E190", "Antalya", "Istanbul", baseDay.AddDays(1).AddHours(18), 15, 280.75m));
            sistemYoneticisi.UcusEkle(new Ucus("Onur Air", "Boeing", "777", "Izmir", "Antalya", baseDay.AddDays(2).AddHours(9).AddMinutes(30), 10, 450.00m));
            sistemYoneticisi.UcusEkle(new Ucus("AtlasGlobal", "Airbus", "A319", "Ankara", "Bodrum", baseDay.AddDays(3).AddHours(16).AddMinutes(15), 12, 400.00m));
            sistemYoneticisi.UcusEkle(new Ucus("Corendon", "Boeing", "737", "Istanbul", "Trabzon", baseDay.AddDays(4).AddHours(13).AddMinutes(45), 18, 325.25m));
            sistemYoneticisi.UcusEkle(new Ucus("Kaya Airlines", "Airbus", "A380", "Antalya", "London", baseDay.AddDays(5).AddHours(8), 30, 950.00m));

            Console.WriteLine("============================================");
            Console.WriteLine("        WELCOME TO KAYA AIRLINES");
            Console.WriteLine("============================================");

            while (true)
            {
                Console.WriteLine("\nMenü:");
                Console.WriteLine("1) Kayıt Ol");
                Console.WriteLine("2) Giriş Yap");
                Console.WriteLine("3) Çıkış");
                Console.Write("Seçim: ");
                string secim = Console.ReadLine() ?? "";

                if (secim == "1")
                {
                    string yeniKullaniciAdi = Helpers.ReadNonEmpty("Kullanıcı Adı: ");
                    string yeniGmail = Helpers.ReadNonEmpty("Gmail: ");
                    string yeniTCKimlik = Helpers.ReadNonEmpty("T.C. Kimlik: ");
                    string yeniSifre = Helpers.ReadNonEmpty("Şifre: ");

                    if (!Kullanici.TCKimlikKontrol(yeniTCKimlik))
                    {
                        Console.WriteLine("Geçersiz T.C. Kimlik. 11 haneli olmalı, rakam olmalı, 0 ile başlamamalı.");
                        continue;
                    }

                    try
                    {
                        sistemYoneticisi.KullaniciKaydet(new Kullanici(yeniKullaniciAdi, yeniGmail, yeniSifre, yeniTCKimlik));
                        Console.WriteLine("✅ Kayıt başarılı!");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Hata: {ex.Message}");
                    }
                }
                else if (secim == "2")
                {
                    string kullaniciAdi = Helpers.ReadNonEmpty("Kullanıcı Adı: ");
                    string sifre = Helpers.ReadNonEmpty("Şifre: ");

                    var kullanici = sistemYoneticisi.GirisYap(kullaniciAdi, sifre);
                    if (kullanici == null)
                    {
                        Console.WriteLine("❌ Giriş başarısız.");
                        continue;
                    }

                    Console.WriteLine("✅ Giriş başarılı!");

                    while (true)
                    {
                        Console.WriteLine("\nKullanıcı Menüsü:");
                        Console.WriteLine("1) Uçuşları Listele");
                        Console.WriteLine("2) Rezervasyon Yap");
                        Console.WriteLine("3) Rezervasyonu Satın Al");
                        Console.WriteLine("4) Direkt Bilet Satın Al");
                        Console.WriteLine("5) Çıkış (Ana Menü)");
                        Console.Write("Seçim: ");
                        string sec2 = Console.ReadLine() ?? "";

                        if (sec2 == "1")
                        {
                            sistemYoneticisi.UcuslariListele();
                        }
                        else if (sec2 == "2")
                        {
                            sistemYoneticisi.UcuslariListele();
                            int no = Helpers.ReadInt("Rezervasyon uçuş no: ", 1, sistemYoneticisi.Ucuslar.Count);
                            Console.WriteLine(await kullanici.RezervasyonYapAsync(sistemYoneticisi.Ucuslar[no - 1]));
                        }
                        else if (sec2 == "3")
                        {
                            sistemYoneticisi.UcuslariListele();
                            int no = Helpers.ReadInt("Satın alma uçuş no: ", 1, sistemYoneticisi.Ucuslar.Count);
                            Console.WriteLine(await kullanici.RezervasyonuSatAsync(sistemYoneticisi.Ucuslar[no - 1]));
                        }
                        else if (sec2 == "4")
                        {
                            sistemYoneticisi.UcuslariListele();
                            int no = Helpers.ReadInt("Direkt bilet uçuş no: ", 1, sistemYoneticisi.Ucuslar.Count);
                            Console.WriteLine(await kullanici.DirektBiletAlAsync(sistemYoneticisi.Ucuslar[no - 1]));
                        }
                        else if (sec2 == "5")
                        {
                            Console.WriteLine("Ana menüye dönüldü.");
                            break;
                        }
                        else
                        {
                            Console.WriteLine("Geçersiz seçim.");
                        }
                    }
                }
                else if (secim == "3")
                {
                    Console.WriteLine("Program kapandı.");
                    return;
                }
                else
                {
                    Console.WriteLine("Geçersiz seçim.");
                }
            }
        }
    }
}
