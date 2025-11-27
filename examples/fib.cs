int Fib(int n){
    if(n<=1)return 1;
    return Fib(n-1)+Fib(n-2);
}

for(var i=0;i<10;++i){
    println(Fib(i).ToString());
}