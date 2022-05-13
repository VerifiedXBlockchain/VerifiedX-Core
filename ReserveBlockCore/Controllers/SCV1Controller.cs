﻿using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Controllers
{
    [Route("scapi/[controller]")]
    [ApiController]
    public class SCV1Controller : ControllerBase
    {
        // GET: api/<V1>
        [HttpGet]
        public IEnumerable<string> Get()
        {
            return new string[] { "Smart", "Contracts", "API" };
        }

        // GET api/<V1>/getgenesisblock
        [HttpGet("{id}")]
        public string Get(string id)
        {
            //use Id to get specific commands
            var output = "Command not recognized."; // this will only display if command not recognized.
            var command = id.ToLower();
            switch (command)
            {
                //This is initial example. Returns Genesis block in JSON format.
                case "getSCData":
                    //Do something later
                    break;
            }

            return output;
        }

        [HttpPost("SCPassTest")]
        public object SCPassTest([FromBody] object jsonData)
        {
            var output = jsonData;

            return output;
        }

        [HttpPost("SCPassDesTest")]
        public string SCPassDesTest([FromBody] object jsonData)
        {
            var output = jsonData.ToString();
            try
            {
                var scMain = JsonConvert.DeserializeObject<SmartContractMain>(jsonData.ToString());

                var json = JsonConvert.SerializeObject(scMain);

                output = json;
            }
            catch (Exception ex)
            {
                output = $"Error - {ex.Message}. Please Try Again.";
            }

            return output;
        }

        [HttpGet("GetAllSmartContracts")]
        public async Task<string> GetAllSmartContracts()
        {
            var output = "";

            var scs = SmartContractMain.SmartContractData.GetSCs().FindAll().ToList();

            if(scs.Count() > 0)
            {
                var json = JsonConvert.SerializeObject(scs);
                output = json;
            }
            else
            {
                output = "null";
            }

            return output;
        }

        [HttpGet("GetSingleSmartContract/{id}")]
        public async Task<string> GetSingleSmartContract(string id)
        {
            var output = "";

            Guid scUID = Guid.Parse(id);

            var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);

            var result = await SmartContractReaderService.ReadSmartContract(sc);

            var scMain = result.Item2;
            var scCode = result.Item1;

            var scInfo = new[]
            {
                new { SmartContract = scMain, SmartContractCode = scCode}
            };

            if (sc != null)
            {
                var json = JsonConvert.SerializeObject(scInfo);
                output = json;
            }
            else
            {
                output = "null";
            }

            return output;
        }

        [HttpPost("CreateSmartContract")]
        public async Task<string> CreateSmartContract([FromBody]object jsonData)
        {
            var output = "";

            try
            {
                var scMain = JsonConvert.DeserializeObject<SmartContractMain>(jsonData.ToString());

                var result = await SmartContractWriterService.WriteSmartContract(scMain);

                SmartContractReturnData scReturnData = new SmartContractReturnData();

                scReturnData.Success = true;
                scReturnData.SmartContractCode = result.Item1;
                scReturnData.SmartContractMain = result.Item2;

                SmartContractMain.SmartContractData.SaveSmartContract(result.Item2, result.Item1);//save smart contract to DB.

                var json = JsonConvert.SerializeObject(scReturnData, Formatting.Indented);

                output = json;
            }
            catch (Exception ex)
            {
                output = $"Error - {ex.Message}. Please Try Again...";
            }
           

            return output;
        }

    }
}
