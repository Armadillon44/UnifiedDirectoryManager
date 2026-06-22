namespace UnifiedDirectoryManager.Services;

/// <summary>
/// One ISO 3166-1 country, matching the three AD attributes ADUC sets together:
/// <c>co</c> (friendly name), <c>c</c> (two-letter code), <c>countryCode</c> (numeric).
/// </summary>
public sealed record CountryInfo(string Name, string Alpha2, int Numeric)
{
    /// <summary>Display label for the picker, e.g. "United States (US)". Empty alpha-2 = the "not set" row.</summary>
    public string Display => string.IsNullOrEmpty(Alpha2) ? Name : $"{Name} ({Alpha2})";
}

/// <summary>ISO 3166-1 reference list for the country picker.</summary>
public static class Countries
{
    /// <summary>Sentinel for clearing the country fields.</summary>
    public static readonly CountryInfo NotSet = new("(not set)", string.Empty, 0);

    /// <summary>All countries, prefixed with the "(not set)" row, ordered by name.</summary>
    public static readonly IReadOnlyList<CountryInfo> All = BuildAll();

    public static CountryInfo? ByAlpha2(string? alpha2) =>
        string.IsNullOrWhiteSpace(alpha2) ? null
        : All.FirstOrDefault(c => string.Equals(c.Alpha2, alpha2, StringComparison.OrdinalIgnoreCase));

    public static CountryInfo? ByName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null
        : All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<CountryInfo> BuildAll()
    {
        var list = new List<CountryInfo>
        {
            new("Afghanistan", "AF", 4), new("Åland Islands", "AX", 248), new("Albania", "AL", 8),
            new("Algeria", "DZ", 12), new("American Samoa", "AS", 16), new("Andorra", "AD", 20),
            new("Angola", "AO", 24), new("Anguilla", "AI", 660), new("Antarctica", "AQ", 10),
            new("Antigua and Barbuda", "AG", 28), new("Argentina", "AR", 32), new("Armenia", "AM", 51),
            new("Aruba", "AW", 533), new("Australia", "AU", 36), new("Austria", "AT", 40),
            new("Azerbaijan", "AZ", 31), new("Bahamas", "BS", 44), new("Bahrain", "BH", 48),
            new("Bangladesh", "BD", 50), new("Barbados", "BB", 52), new("Belarus", "BY", 112),
            new("Belgium", "BE", 56), new("Belize", "BZ", 84), new("Benin", "BJ", 204),
            new("Bermuda", "BM", 60), new("Bhutan", "BT", 64), new("Bolivia", "BO", 68),
            new("Bonaire, Sint Eustatius and Saba", "BQ", 535), new("Bosnia and Herzegovina", "BA", 70),
            new("Botswana", "BW", 72), new("Bouvet Island", "BV", 74), new("Brazil", "BR", 76),
            new("British Indian Ocean Territory", "IO", 86), new("Brunei Darussalam", "BN", 96),
            new("Bulgaria", "BG", 100), new("Burkina Faso", "BF", 854), new("Burundi", "BI", 108),
            new("Cabo Verde", "CV", 132), new("Cambodia", "KH", 116), new("Cameroon", "CM", 120),
            new("Canada", "CA", 124), new("Cayman Islands", "KY", 136), new("Central African Republic", "CF", 140),
            new("Chad", "TD", 148), new("Chile", "CL", 152), new("China", "CN", 156),
            new("Christmas Island", "CX", 162), new("Cocos (Keeling) Islands", "CC", 166), new("Colombia", "CO", 170),
            new("Comoros", "KM", 174), new("Congo", "CG", 178), new("Congo (Democratic Republic)", "CD", 180),
            new("Cook Islands", "CK", 184), new("Costa Rica", "CR", 188), new("Côte d'Ivoire", "CI", 384),
            new("Croatia", "HR", 191), new("Cuba", "CU", 192), new("Curaçao", "CW", 531),
            new("Cyprus", "CY", 196), new("Czechia", "CZ", 203), new("Denmark", "DK", 208),
            new("Djibouti", "DJ", 262), new("Dominica", "DM", 212), new("Dominican Republic", "DO", 214),
            new("Ecuador", "EC", 218), new("Egypt", "EG", 818), new("El Salvador", "SV", 222),
            new("Equatorial Guinea", "GQ", 226), new("Eritrea", "ER", 232), new("Estonia", "EE", 233),
            new("Eswatini", "SZ", 748), new("Ethiopia", "ET", 231), new("Falkland Islands", "FK", 238),
            new("Faroe Islands", "FO", 234), new("Fiji", "FJ", 242), new("Finland", "FI", 246),
            new("France", "FR", 250), new("French Guiana", "GF", 254), new("French Polynesia", "PF", 258),
            new("French Southern Territories", "TF", 260), new("Gabon", "GA", 266), new("Gambia", "GM", 270),
            new("Georgia", "GE", 268), new("Germany", "DE", 276), new("Ghana", "GH", 288),
            new("Gibraltar", "GI", 292), new("Greece", "GR", 300), new("Greenland", "GL", 304),
            new("Grenada", "GD", 308), new("Guadeloupe", "GP", 312), new("Guam", "GU", 316),
            new("Guatemala", "GT", 320), new("Guernsey", "GG", 831), new("Guinea", "GN", 324),
            new("Guinea-Bissau", "GW", 624), new("Guyana", "GY", 328), new("Haiti", "HT", 332),
            new("Heard Island and McDonald Islands", "HM", 334), new("Holy See", "VA", 336), new("Honduras", "HN", 340),
            new("Hong Kong", "HK", 344), new("Hungary", "HU", 348), new("Iceland", "IS", 352),
            new("India", "IN", 356), new("Indonesia", "ID", 360), new("Iran", "IR", 364),
            new("Iraq", "IQ", 368), new("Ireland", "IE", 372), new("Isle of Man", "IM", 833),
            new("Israel", "IL", 376), new("Italy", "IT", 380), new("Jamaica", "JM", 388),
            new("Japan", "JP", 392), new("Jersey", "JE", 832), new("Jordan", "JO", 400),
            new("Kazakhstan", "KZ", 398), new("Kenya", "KE", 404), new("Kiribati", "KI", 296),
            new("Korea (North)", "KP", 408), new("Korea (South)", "KR", 410), new("Kuwait", "KW", 414),
            new("Kyrgyzstan", "KG", 417), new("Laos", "LA", 418), new("Latvia", "LV", 428),
            new("Lebanon", "LB", 422), new("Lesotho", "LS", 426), new("Liberia", "LR", 430),
            new("Libya", "LY", 434), new("Liechtenstein", "LI", 438), new("Lithuania", "LT", 440),
            new("Luxembourg", "LU", 442), new("Macao", "MO", 446), new("Madagascar", "MG", 450),
            new("Malawi", "MW", 454), new("Malaysia", "MY", 458), new("Maldives", "MV", 462),
            new("Mali", "ML", 466), new("Malta", "MT", 470), new("Marshall Islands", "MH", 584),
            new("Martinique", "MQ", 474), new("Mauritania", "MR", 478), new("Mauritius", "MU", 480),
            new("Mayotte", "YT", 175), new("Mexico", "MX", 484), new("Micronesia", "FM", 583),
            new("Moldova", "MD", 498), new("Monaco", "MC", 492), new("Mongolia", "MN", 496),
            new("Montenegro", "ME", 499), new("Montserrat", "MS", 500), new("Morocco", "MA", 504),
            new("Mozambique", "MZ", 508), new("Myanmar", "MM", 104), new("Namibia", "NA", 516),
            new("Nauru", "NR", 520), new("Nepal", "NP", 524), new("Netherlands", "NL", 528),
            new("New Caledonia", "NC", 540), new("New Zealand", "NZ", 554), new("Nicaragua", "NI", 558),
            new("Niger", "NE", 562), new("Nigeria", "NG", 566), new("Niue", "NU", 570),
            new("Norfolk Island", "NF", 574), new("North Macedonia", "MK", 807), new("Northern Mariana Islands", "MP", 580),
            new("Norway", "NO", 578), new("Oman", "OM", 512), new("Pakistan", "PK", 586),
            new("Palau", "PW", 585), new("Palestine", "PS", 275), new("Panama", "PA", 591),
            new("Papua New Guinea", "PG", 598), new("Paraguay", "PY", 600), new("Peru", "PE", 604),
            new("Philippines", "PH", 608), new("Pitcairn", "PN", 612), new("Poland", "PL", 616),
            new("Portugal", "PT", 620), new("Puerto Rico", "PR", 630), new("Qatar", "QA", 634),
            new("Réunion", "RE", 638), new("Romania", "RO", 642), new("Russian Federation", "RU", 643),
            new("Rwanda", "RW", 646), new("Saint Barthélemy", "BL", 652), new("Saint Helena", "SH", 654),
            new("Saint Kitts and Nevis", "KN", 659), new("Saint Lucia", "LC", 662), new("Saint Martin (French)", "MF", 663),
            new("Saint Pierre and Miquelon", "PM", 666), new("Saint Vincent and the Grenadines", "VC", 670),
            new("Samoa", "WS", 882), new("San Marino", "SM", 674), new("Sao Tome and Principe", "ST", 678),
            new("Saudi Arabia", "SA", 682), new("Senegal", "SN", 686), new("Serbia", "RS", 688),
            new("Seychelles", "SC", 690), new("Sierra Leone", "SL", 694), new("Singapore", "SG", 702),
            new("Sint Maarten (Dutch)", "SX", 534), new("Slovakia", "SK", 703), new("Slovenia", "SI", 705),
            new("Solomon Islands", "SB", 90), new("Somalia", "SO", 706), new("South Africa", "ZA", 710),
            new("South Georgia and the South Sandwich Islands", "GS", 239), new("South Sudan", "SS", 728),
            new("Spain", "ES", 724), new("Sri Lanka", "LK", 144), new("Sudan", "SD", 729),
            new("Suriname", "SR", 740), new("Svalbard and Jan Mayen", "SJ", 744), new("Sweden", "SE", 752),
            new("Switzerland", "CH", 756), new("Syrian Arab Republic", "SY", 760), new("Taiwan", "TW", 158),
            new("Tajikistan", "TJ", 762), new("Tanzania", "TZ", 834), new("Thailand", "TH", 764),
            new("Timor-Leste", "TL", 626), new("Togo", "TG", 768), new("Tokelau", "TK", 772),
            new("Tonga", "TO", 776), new("Trinidad and Tobago", "TT", 780), new("Tunisia", "TN", 788),
            new("Türkiye", "TR", 792), new("Turkmenistan", "TM", 795), new("Turks and Caicos Islands", "TC", 796),
            new("Tuvalu", "TV", 798), new("Uganda", "UG", 800), new("Ukraine", "UA", 804),
            new("United Arab Emirates", "AE", 784), new("United Kingdom", "GB", 826), new("United States", "US", 840),
            new("United States Minor Outlying Islands", "UM", 581), new("Uruguay", "UY", 858), new("Uzbekistan", "UZ", 860),
            new("Vanuatu", "VU", 548), new("Venezuela", "VE", 862), new("Viet Nam", "VN", 704),
            new("Virgin Islands (British)", "VG", 92), new("Virgin Islands (U.S.)", "VI", 850),
            new("Wallis and Futuna", "WF", 876), new("Western Sahara", "EH", 732), new("Yemen", "YE", 887),
            new("Zambia", "ZM", 894), new("Zimbabwe", "ZW", 716),
        };
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return new[] { NotSet }.Concat(list).ToList();
    }
}
