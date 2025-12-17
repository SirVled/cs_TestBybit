using System;
using System.Collections.Generic;
using System.Text;

namespace cs_TestBybit.Models
{
    public record ExchangeOption(
        string ApiKey,
        string ApiSecret,
        string PrivateWsUrl,
        string BaseRestUrl
 );
}
